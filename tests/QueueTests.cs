using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using ChaoChaoKvm.Core;
using ChaoChaoKvm.Services;
public class QueueTests {
 static Dictionary<string,object> Item(string kind,int id){return new Dictionary<string,object>{{"kind",kind},{"id",id},{"down",false}};}
 public static int Main(){try{using(var server=new HttpPeerServer()){
  server.Start(0,IPAddress.Loopback);
  server.QueueInput(Item("key",0));for(int i=1;i<80;i++)server.QueueInput(Item("move",i));server.QueueInput(Item("button",80));server.QueueInput(Item("key",81));
  var request=(HttpWebRequest)WebRequest.Create("http://127.0.0.1:"+server.Port+"/api/v1/kvm/pull/input?batch=1");request.Proxy=null;
  using(var response=request.GetResponse())using(var reader=new StreamReader(response.GetResponseStream())){
   var data=JsonUtil.Parse(reader.ReadToEnd());var payloads=(System.Collections.IEnumerable)data["payloads"];var ids=new List<int>();foreach(Dictionary<string,object> item in payloads)ids.Add(Convert.ToInt32(item["id"]));
   if(string.Join(",",ids)!="0,79,80,81")throw new Exception("mouse queue dropped/reordered key/button release");
  }
  server.QueueInput(Item("key",82));request=(HttpWebRequest)WebRequest.Create("http://127.0.0.1:"+server.Port+"/api/v1/kvm/pull/input");request.Proxy=null;
  using(var response=request.GetResponse())using(var reader=new StreamReader(response.GetResponseStream())){var data=JsonUtil.Parse(reader.ReadToEnd());if(Convert.ToInt32(((Dictionary<string,object>)data["payload"])["id"])!=82)throw new Exception("legacy payload mismatch");}
  // Exercise legacy clients' Expect header using a socket, without a framework fallback timer.
  using(var tcp=new TcpClient("127.0.0.1",server.Port)){tcp.ReceiveTimeout=1000;var stream=tcp.GetStream();var headers=System.Text.Encoding.ASCII.GetBytes("POST /api/v1/heartbeat HTTP/1.1\r\nHost: localhost\r\nContent-Length: 2\r\nExpect: 100-continue\r\n\r\n");stream.Write(headers,0,headers.Length);var buffer=new byte[25];int got=0;while(got<25)got+=stream.Read(buffer,got,25-got);if(System.Text.Encoding.ASCII.GetString(buffer)!="HTTP/1.1 100 Continue\r\n\r\n")throw new Exception("missing continue response");var body=System.Text.Encoding.ASCII.GetBytes("{}");stream.Write(body,0,2);using(var reader=new StreamReader(stream)){if(!reader.ReadToEnd().Contains("200 OK"))throw new Exception("heartbeat failed");}}
 }Console.WriteLine("QUEUE_AND_LEGACY_CONTINUE_PASS");return 0;}catch(Exception e){Console.WriteLine(e);return 1;}}
}
