using ChaoChaoKvm.Services;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

public static class KvmLoopbackSmoke
{
    public static int Main()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        TcpClient accepted = null;
        Thread acceptThread = new Thread(new ThreadStart(delegate { accepted = listener.AcceptTcpClient(); }));
        acceptThread.Start();

        TcpClient client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        acceptThread.Join(3000);
        listener.Stop();

        if (accepted == null)
        {
            Console.WriteLine("FAIL accept");
            return 2;
        }

        ScreenCaptureService capture = new ScreenCaptureService();
        InputInjectionService input = new InputInjectionService();
        PeerSession a = new PeerSession(client, true, capture, input);
        PeerSession b = new PeerSession(accepted, false, capture, input);
        a.AllowRemoteControl = false;
        b.AllowRemoteControl = false;
        a.Fps = 4;
        b.Fps = 4;
        a.JpegQuality = 35;
        b.JpegQuality = 35;

        int aFrames = 0;
        int bFrames = 0;
        bool aInfo = false;
        bool bInfo = false;
        a.RemoteFrame += delegate(byte[] data) { if (data != null && data.Length > 1024) Interlocked.Increment(ref aFrames); };
        b.RemoteFrame += delegate(byte[] data) { if (data != null && data.Length > 1024) Interlocked.Increment(ref bFrames); };
        a.RemoteInfoChanged += delegate(ScreenInfo info) { if (info != null && info.Width > 0) aInfo = true; };
        b.RemoteInfoChanged += delegate(ScreenInfo info) { if (info != null && info.Width > 0) bInfo = true; };

        a.Start();
        b.Start();

        DateTime until = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < until && (aFrames < 2 || bFrames < 2 || !aInfo || !bInfo))
            Thread.Sleep(100);

        a.Dispose();
        b.Dispose();

        Console.WriteLine("A_FRAMES=" + aFrames);
        Console.WriteLine("B_FRAMES=" + bFrames);
        Console.WriteLine("A_INFO=" + aInfo);
        Console.WriteLine("B_INFO=" + bInfo);

        if (aFrames >= 2 && bFrames >= 2 && aInfo && bInfo)
        {
            Console.WriteLine("KVM_LOOPBACK_PASS");
            return 0;
        }
        Console.WriteLine("KVM_LOOPBACK_FAIL");
        return 3;
    }
}
