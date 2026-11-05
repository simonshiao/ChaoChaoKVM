# ChaoChao KVM Cloudflare HTTPS relay

This Worker carries the existing KVM HTTP protocol over outbound WebSockets on TLS port 443. The desktop app connects to it through the Windows system HTTP proxy when one is enabled. The KVM LAN listener and LAN discovery are not changed.

## Cloudflare setup

1. From this directory, run `npm install` and `npx wrangler login` with the Cloudflare account that owns `chaochao.win`.
2. Review `wrangler.toml` and set `ACCESS_EMAIL` to the exact account email that should be permitted.
3. Run `npx wrangler deploy`. This creates the `kvm-relay.chaochao.win` Custom Domain. If that hostname already has a DNS record, resolve that conflict before deployment; do not change `kvm.chaochao.win` or existing MCP records.
4. In Cloudflare Zero Trust, create or update a **self-hosted hostname Access application** for exactly `kvm-relay.chaochao.win`. Keep an Allow policy for the one account email and add a Service Auth policy that includes only the HOME-1 and TW service tokens. Do not use Worker-level Access for this Worker: Cloudflare documents that Worker-level Access does not support WebSocket upgrade requests.
5. Set the Worker's `ACCESS_SERVICE_TOKEN_IDS` binding to the two service token Client IDs, comma-separated. These IDs are not Client Secrets. The Worker accepts either the configured account email or an Access-authenticated service token whose ID is on this allowlist; the Durable Object repeats the same check. Never put Client Secrets in Worker variables, source control, or logs.
6. Verify `/health` through Cloudflare Access (for example, after signing in with the allowed account); it should return `{"ok":true,"relay":"chaokvm"}`. Keep `/relay` behind the same hostname Access app.
7. Save each machine's own Client ID and Client Secret in its Recovery KVM **網路連線 → Cloudflare 免 Google 登入設定**. The app stores them with Windows DPAPI. The Recovery UI's **VPN 443 中繼** page can then start the host or accept a `CCF2.` pairing code.

The hostname must be a separate Worker Custom Domain. Do not attach the Worker to the existing `kvm.chaochao.win` tunnel hostname. Custom Domains create DNS and certificates for the Worker, and hostname-based Access protects the WebSocket endpoint. See [Cloudflare Custom Domains](https://developers.cloudflare.com/workers/configuration/routing/custom-domains/) and [Cloudflare Access for Workers](https://developers.cloudflare.com/workers/configuration/cloudflare-access/).

## Local validation

Run `npm install` once, then `npm run check` to bundle the Worker with Wrangler without deploying it. For the desktop app, build the `CloudflareBuild=true` flavor from `src/ChaoChaoKvm.Net472/ChaoChaoKvm.Net472.csproj`.

This relay is not available until the Worker Custom Domain and hostname-based Access application are deployed. Cloudflare plan quotas and Durable Object limits apply; see [Durable Objects pricing](https://developers.cloudflare.com/durable-objects/platform/pricing/) and [limits](https://developers.cloudflare.com/durable-objects/platform/limits/).
