using System.Net;
using System.Net.Sockets;
using System.IO.Compression;
using OpenVNCViewer;

foreach(int encoding in new[]{0,16})
{
    var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
    int port=((IPEndPoint)listener.LocalEndpoint).Port;
    var server=Task.Run(async()=>
    {
        using var peer=await listener.AcceptTcpClientAsync();using var s=peer.GetStream();s.ReadTimeout=5000;
        void Read(int n){s.ReadExactly(new byte[n]);}
        s.Write("RFB 003.008\n"u8);Read(12);s.Write(new byte[]{1,1});Read(1);s.Write(new byte[4]);Read(1);
        s.Write(new byte[]{0,2,0,2});s.Write(new byte[16]);s.Write(new byte[4]);Read(20);Read(20);Read(10);
        s.Write(new byte[]{0,0,0,1,0,0,0,0,0,2,0,2,0,0,0,(byte)encoding});
        if(encoding==0)s.Write(new byte[]{1,2,3,0,1,2,3,0,1,2,3,0,1,2,3,0});
        else
        {
            using var bytes=new MemoryStream();using(var z=new ZLibStream(bytes,CompressionLevel.Fastest,true))z.Write(new byte[]{1,1,2,3});
            byte[] packed=bytes.ToArray();byte[] len=new byte[4];System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len,packed.Length);s.Write(len);s.Write(packed);
        }
        Read(10);
    });
    using var c=new RfbClient();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await c.ConnectAsync("127.0.0.1",port,"","",false,timeout.Token);
    try{await Task.Run(()=>c.Run(timeout.Token));}catch(EndOfStreamException){}
    await server;listener.Stop();
    if(c.Width!=2||c.Height!=2||c.Updates!=1||!c.Pixels.SequenceEqual(new byte[]{1,2,3,0,1,2,3,0,1,2,3,0,1,2,3,0}))throw new Exception($"Pixel test failed: {encoding}");
    Console.WriteLine($"PASS: handshake and encoding {encoding}");
}
