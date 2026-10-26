interface AccessIdentity {
  email?: string;
}

interface AccessContext extends ExecutionContext {
  access?: {
    getIdentity(): Promise<AccessIdentity | undefined>;
  };
}

interface Env {
  RELAY_ROOMS: DurableObjectNamespace<KvmRelayRoom>;
  ACCESS_EMAIL: string;
}

interface SocketAttachment {
  role: "host" | "client";
  streamId?: string;
}

const ROOM_ID = /^[a-f0-9]{32}$/;
const SECRET = /^[A-Za-z0-9_-]{40,80}$/;
const STREAM_ID = /^[a-f0-9]{32}$/;
const MAX_FRAME_BYTES = 64 * 1024;
const HEADER_BYTES = 16;

export default {
  async fetch(request: Request, env: Env, ctx: AccessContext): Promise<Response> {
    const url = new URL(request.url);
    if (url.pathname === "/health" && request.method === "GET") {
      return Response.json({ ok: true, relay: "chaokvm" });
    }
    if (url.pathname !== "/relay" || request.method !== "GET") {
      return new Response("Not found", { status: 404 });
    }

    const identity = await ctx.access?.getIdentity();
    if (!identity?.email || identity.email.toLowerCase() !== env.ACCESS_EMAIL.toLowerCase()) {
      return new Response("Cloudflare Access authorization required", { status: 403 });
    }
    if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") {
      return new Response("WebSocket upgrade required", { status: 426 });
    }

    const room = url.searchParams.get("room") || "";
    const role = url.searchParams.get("role") || "";
    if (!ROOM_ID.test(room) || (role !== "host" && role !== "client")) {
      return new Response("Invalid relay room", { status: 400 });
    }
    const secret = request.headers.get(role === "host" ? "X-KVM-Host-Key" : "X-KVM-Pair-Key") || "";
    if (!SECRET.test(secret)) return new Response("Invalid relay credential", { status: 403 });

    const stub = env.RELAY_ROOMS.get(env.RELAY_ROOMS.idFromName(room));
    const headers = new Headers(request.headers);
    headers.set("X-KVM-Role", role);
    headers.set("X-KVM-Room-Secret", secret);
    headers.set("X-KVM-Access-Email", identity.email);
    return stub.fetch(new Request("https://relay.internal/room", { method: "GET", headers }));
  },
};

export class KvmRelayRoom {
  constructor(private readonly state: DurableObjectState, private readonly env: Env) {}

  async fetch(request: Request): Promise<Response> {
    if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") {
      return new Response("WebSocket upgrade required", { status: 426 });
    }
    const role = request.headers.get("X-KVM-Role") as SocketAttachment["role"] | null;
    const secret = request.headers.get("X-KVM-Room-Secret") || "";
    const email = request.headers.get("X-KVM-Access-Email") || "";
    if ((role !== "host" && role !== "client") || !email || email.toLowerCase() !== this.env.ACCESS_EMAIL.toLowerCase()) {
      return new Response("Unauthorized", { status: 403 });
    }

    const storedHash = await this.state.storage.get<string>("secretHash");
    const suppliedHash = await hashSecret(secret);
    if (role === "host") {
      const hosts = this.state.getWebSockets("host").filter(socket => socket.readyState === WebSocket.OPEN);
      if (hosts.length) return new Response("Host is already connected", { status: 409 });
      if (storedHash && !constantTimeEqual(storedHash, suppliedHash)) {
        return new Response("Relay host key mismatch", { status: 403 });
      }
      if (!storedHash) await this.state.storage.put("secretHash", suppliedHash);
    } else {
      if (!storedHash || !constantTimeEqual(storedHash, suppliedHash)) {
        return new Response("Relay pairing code is invalid", { status: 403 });
      }
      if (!this.state.getWebSockets("host").some(socket => socket.readyState === WebSocket.OPEN)) {
        return new Response("Relay host is offline", { status: 503 });
      }
    }

    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    const streamId = role === "client" ? crypto.randomUUID().replace(/-/g, "") : undefined;
    this.state.acceptWebSocket(server, role === "host" ? ["host"] : ["client", streamId!]);
    server.serializeAttachment({ role, streamId } satisfies SocketAttachment);
    if (role === "client") {
      const host = this.state.getWebSockets("host").find(socket => socket.readyState === WebSocket.OPEN);
      if (!host) {
        server.close(1013, "Relay host is offline");
        return new Response("Relay host is offline", { status: 503 });
      }
      try { host.send("OPEN " + streamId); }
      catch {
        server.close(1013, "Relay host unavailable");
        return new Response("Relay host is unavailable", { status: 503 });
      }
    }
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(socket: WebSocket, message: string | ArrayBuffer): Promise<void> {
    const attachment = socket.deserializeAttachment() as SocketAttachment | null;
    if (!attachment) { socket.close(1011, "Missing relay state"); return; }
    const sockets = this.state.getWebSockets();
    const host = sockets.find(candidate => {
      const item = candidate.deserializeAttachment() as SocketAttachment | null;
      return item?.role === "host" && candidate.readyState === WebSocket.OPEN;
    });

    if (attachment.role === "host") {
      if (typeof message === "string") {
        const match = /^(OPENED|FAILED|CLOSE) ([a-f0-9]{32})(?:\s+(.*))?$/.exec(message);
        if (!match) { socket.close(1002, "Invalid relay control frame"); return; }
        const client = sockets.find(candidate => {
          const item = candidate.deserializeAttachment() as SocketAttachment | null;
          return item?.role === "client" && item.streamId === match[2] && candidate.readyState === WebSocket.OPEN;
        });
        if (client) {
          try { client.send(match[1] + " " + match[2] + (match[3] ? " " + match[3].slice(0, 160) : "")); }
          catch { client.close(1011, "Relay delivery failed"); }
        }
        if (match[1] === "CLOSE" && client) client.close(1000, "Host closed stream");
        return;
      }
      const frame = new Uint8Array(message);
      if (frame.byteLength < HEADER_BYTES || frame.byteLength > MAX_FRAME_BYTES + HEADER_BYTES) {
        socket.close(1009, "Relay frame size invalid"); return;
      }
      const streamId = bytesToHex(frame.subarray(0, HEADER_BYTES));
      const client = sockets.find(candidate => {
        const item = candidate.deserializeAttachment() as SocketAttachment | null;
        return item?.role === "client" && item.streamId === streamId && candidate.readyState === WebSocket.OPEN;
      });
      if (client) {
        try { client.send(frame.subarray(HEADER_BYTES)); }
        catch { client.close(1011, "Relay delivery failed"); }
      }
      return;
    }

    if (!host) { socket.close(1013, "Relay host disconnected"); return; }
    if (typeof message === "string") { socket.close(1002, "Invalid relay data frame"); return; }
    const frame = new Uint8Array(message);
    if (frame.byteLength > MAX_FRAME_BYTES) { socket.close(1009, "Relay frame too large"); return; }
    const idBytes = hexToBytes(attachment.streamId || "");
    if (idBytes.byteLength !== HEADER_BYTES) { socket.close(1011, "Invalid stream id"); return; }
    const forwarded = new Uint8Array(HEADER_BYTES + frame.byteLength);
    forwarded.set(idBytes, 0);
    forwarded.set(frame, HEADER_BYTES);
    try { host.send(forwarded); }
    catch { socket.close(1011, "Relay host unavailable"); }
  }

  async webSocketClose(socket: WebSocket, code: number, reason: string): Promise<void> {
    const attachment = socket.deserializeAttachment() as SocketAttachment | null;
    if (!attachment) return;
    if (attachment.role === "host") {
      for (const peer of this.state.getWebSockets("client")) {
        try { peer.close(1012, "Relay host disconnected"); } catch { }
      }
      return;
    }
    const host = this.state.getWebSockets("host").find(peer => peer.readyState === WebSocket.OPEN);
    if (host && attachment.streamId) {
      try { host.send("CLOSE " + attachment.streamId); } catch { }
    }
  }

  async webSocketError(socket: WebSocket): Promise<void> {
    try { socket.close(1011, "Relay socket error"); } catch { }
  }
}

async function hashSecret(secret: string): Promise<string> {
  const bytes = new TextEncoder().encode(secret);
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return bytesToHex(new Uint8Array(digest));
}

function constantTimeEqual(left: string, right: string): boolean {
  if (left.length !== right.length) return false;
  let difference = 0;
  for (let index = 0; index < left.length; index++) difference |= left.charCodeAt(index) ^ right.charCodeAt(index);
  return difference === 0;
}

function bytesToHex(bytes: Uint8Array): string {
  return Array.from(bytes, value => value.toString(16).padStart(2, "0")).join("");
}

function hexToBytes(value: string): Uint8Array {
  if (!STREAM_ID.test(value)) return new Uint8Array(0);
  const bytes = new Uint8Array(value.length / 2);
  for (let index = 0; index < bytes.length; index++) bytes[index] = parseInt(value.slice(index * 2, index * 2 + 2), 16);
  return bytes;
}
