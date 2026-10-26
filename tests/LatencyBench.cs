using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using ChaoChaoKvm.Services;

public class LatencyBench
{
    static void Wait(Func<bool> condition, int ms, string name) { var w=Stopwatch.StartNew(); while(!condition() && w.ElapsedMilliseconds<ms) Thread.Sleep(2); if(!condition()) throw new Exception("timeout: "+name); }
    static double Percentile(List<double> data,double p) { lock(data) { var copy=data.ToArray(); Array.Sort(copy); return copy.Length==0 ? -1 : copy[(int)((copy.Length-1)*p)]; } }
    public static int Main(string[] args)
    {
        bool verify=args.Length>0 && args[0]=="verify";
        var server=new HttpPeerServer(); var client=new HttpPeerClient(); var publisherStop=new ManualResetEvent(false); Thread publisher=null;
        string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"latency-fixture",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try {
            var start=typeof(HttpPeerServer).GetMethod("Start",new[]{typeof(int),typeof(IPAddress)});
            if(start!=null) start.Invoke(server,new object[]{0,IPAddress.Loopback}); else server.Start(0);
            var listener=(TcpListener)typeof(HttpPeerServer).GetField("_listener",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(server);
            int port=((IPEndPoint)listener.LocalEndpoint).Port;
            server.ReceiveDirectory=dir; client.ReceiveDirectory=Path.Combine(dir,"received");
            var info=new ScreenInfo{Width=1920,Height=1080,Machine="SYNTHETIC TEST"};
            publisher=new Thread(()=>{while(!publisherStop.WaitOne(0)){var frame=new byte[128*1024]; Buffer.BlockCopy(BitConverter.GetBytes(DateTime.UtcNow.Ticks),0,frame,0,8); server.SetLocalFrame(frame,info); if(publisherStop.WaitOne(50))break;}}){IsBackground=true}; publisher.Start();
            var ages=new List<double>(); int frames=0, inputs=0; var ids=new List<int>();
            client.RemoteFrame+=frame=>{Interlocked.Increment(ref frames); if(frame.Length>=8)lock(ages)ages.Add((DateTime.UtcNow.Ticks-BitConverter.ToInt64(frame,0))/10000.0);};
            client.InputReceived+=input=>{lock(ids)ids.Add(Convert.ToInt32(input["id"])); Interlocked.Increment(ref inputs);};
            client.SetFrameRate(20); var startup=Stopwatch.StartNew(); client.Start("127.0.0.1",port); Wait(()=>client.Connected,4000,"connect");
            Console.WriteLine("CONNECT_MS="+startup.ElapsedMilliseconds);
            var pushTimes=new List<double>();
            for(int i=0;i<12;i++){var sw=Stopwatch.StartNew(); if(!client.SendInput(new Dictionary<string,object>{{"kind","probe"},{"id",i}}))throw new Exception("input push failed");pushTimes.Add(sw.Elapsed.TotalMilliseconds);}
            Console.WriteLine("DIRECT_INPUT_P50_MS="+Percentile(pushTimes,.5).ToString("F2"));
            Console.WriteLine("DIRECT_INPUT_P95_MS="+Percentile(pushTimes,.95).ToString("F2"));
            var burst=Stopwatch.StartNew(); for(int i=0;i<32;i++) server.QueueInput(new Dictionary<string,object>{{"kind","key"},{"id",i},{"down",i%2==0}});
            Wait(()=>inputs>=32,7000,"reverse input burst"); long burstMs=burst.ElapsedMilliseconds;
            lock(ids)for(int i=0;i<32;i++)if(ids[i]!=i)throw new Exception("reverse input ordering failure");
            Console.WriteLine("REVERSE_32_EVENTS_MS="+burstMs);
            int delayed=0; var downloadStarted=new ManualResetEvent(false);
            server.FileProgress+=(a,b,n)=>{if(Interlocked.CompareExchange(ref delayed,1,0)==0){downloadStarted.Set();Thread.Sleep(1500);}};
            string fixture=Path.Combine(dir,"synthetic.bin"); var fileBytes=new byte[1024*1024]; for(int i=0;i<fileBytes.Length;i++)fileBytes[i]=(byte)(i%251); File.WriteAllBytes(fixture,fileBytes); server.QueueFile(fixture,"synthetic.bin");
            if(!downloadStarted.WaitOne(4000))throw new Exception("download never began");
            int framesBefore=frames; server.QueueInput(new Dictionary<string,object>{{"kind","key"},{"id",32},{"down",false}});
            var during=Stopwatch.StartNew(); Wait(()=>inputs>=33,5000,"input during bulk transfer"); long bulkInputMs=during.ElapsedMilliseconds;
            Thread.Sleep(300); int concurrentFrames=frames-framesBefore;
            Console.WriteLine("INPUT_DURING_SLOW_FILE_MS="+bulkInputMs);
            Console.WriteLine("FRAMES_DURING_FILE_WINDOW="+concurrentFrames);
            Wait(()=>File.Exists(Path.Combine(client.ReceiveDirectory,"synthetic.bin")),5000,"file complete");
            var received=File.ReadAllBytes(Path.Combine(client.ReceiveDirectory,"synthetic.bin"));
            if(received.Length!=fileBytes.Length)throw new Exception("file size mismatch");
            for(int i=0;i<received.Length;i++)if(received[i]!=fileBytes[i])throw new Exception("file content mismatch");
            Thread.Sleep(300); Console.WriteLine("FRAME_AGE_P95_MS="+Percentile(ages,.95).ToString("F2"));
            client.Stop(); Thread.Sleep(150); int stoppedFrames=frames; Thread.Sleep(200); if(frames!=stoppedFrames)throw new Exception("frames after stop");
            for(int i=0;i<8;i++)client.Start("127.0.0.1",port);
            Wait(()=>client.Connected,4000,"rapid restart"); Thread.Sleep(150); client.Stop(); Thread.Sleep(200);
            if(verify && (burstMs>600 || bulkInputMs>500 || concurrentFrames<2))throw new Exception("latency regression budget exceeded");
            Console.WriteLine("TRANSPORT_BENCH_PASS synthetic loopback; no screen capture or OS input"); return 0;
        } catch(Exception e){Console.WriteLine(e);return 1;}
        finally{publisherStop.Set();if(publisher!=null)publisher.Join(200);client.Dispose();server.Dispose();}
    }
}
