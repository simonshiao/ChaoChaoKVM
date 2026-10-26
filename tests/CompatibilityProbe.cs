using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using ChaoChaoKvm.Services;

public class CompatibilityProbe
{
    public static int Main(string[] args)
    {
        if(args[0]=="server")
        {
            using(var server=new HttpPeerServer()) {
                var start=typeof(HttpPeerServer).GetMethod("Start",new[]{typeof(int),typeof(IPAddress)});
                if(start!=null) start.Invoke(server,new object[]{0,IPAddress.Loopback});else server.Start(0);
                var listener=(TcpListener)typeof(HttpPeerServer).GetField("_listener",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(server);
                server.SetLocalFrame(new byte[128],new ScreenInfo{Width=100,Height=100,Machine="COMPATIBILITY"});
                server.QueueInput(new Dictionary<string,object>{{"kind","key"},{"id",17},{"down",false}});
                Console.WriteLine(((IPEndPoint)listener.LocalEndpoint).Port); Console.Out.Flush(); Console.ReadLine();
            } return 0;
        }
        using(var process=new Process())
        using(var client=new HttpPeerClient())
        {
            process.StartInfo=new ProcessStartInfo(args[0],"server"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true};
            process.Start();
            try {
                int port=int.Parse(process.StandardOutput.ReadLine()); int frames=0,inputs=0;
                client.RemoteFrame+=data=>Interlocked.Increment(ref frames);
                client.InputReceived+=data=>{if(Convert.ToInt32(data["id"])==17)Interlocked.Increment(ref inputs);};
                client.Start("127.0.0.1",port); var sw=Stopwatch.StartNew();
                while((frames==0 || inputs==0 || !client.Connected) && sw.ElapsedMilliseconds<5000)Thread.Sleep(10);
                if(frames==0 || inputs!=1 || !client.SendInput(new Dictionary<string,object>{{"kind","probe"}})) throw new Exception("mixed-version frame/input failure");
                Console.WriteLine("MIXED_VERSION_PASS frame, reverse input, direct input"); return 0;
            } catch(Exception e){Console.WriteLine(e);return 1;}
            finally{client.Stop();process.StandardInput.WriteLine("stop");process.WaitForExit(3000);}
        }
    }
}
