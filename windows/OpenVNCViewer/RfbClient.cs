// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace OpenVNCViewer;

// One decoder thread owns protocol state. A single framebuffer is protected by Gate;
// the UI consumes only the latest completed update, with no queued frame copies.
public sealed class RfbAuthenticationException(string message) : IOException(message) { }

public sealed class RfbClient : IDisposable
{
    readonly TcpClient tcp = new() { NoDelay = true };
    NetworkStream stream = null!;
    readonly object sendGate = new();
    volatile ViewerOptions options = new();
    bool sixteenBit;
    int forceRefresh;
    public string ActiveEncoding { get; private set; } = "等待画面";
    public int ActiveColorBits => sixteenBit ? 16 : 24;
    public bool WasConnected { get; private set; }
    public event Action? BellReceived;
    public void ApplyLiveOptions(ViewerOptions value) => options = value.Normalize();
    public void Refresh() => Interlocked.Exchange(ref forceRefresh, 1);
    public readonly object Gate = new();
    public int Width { get; private set; }
    public int Height { get; private set; }
    public byte[] Pixels { get; private set; } = [];
    public long Generation { get; private set; }
    public long Updates { get; private set; }
    public long ReceivedBytes;
    public (int X,int Y,int W,int H)? Dirty;
    void Mark(int x,int y,int w,int h)
    {
        if(Dirty is var d && d.HasValue){var a=d.Value;int right=Math.Max(a.X+a.W,x+w),bottom=Math.Max(a.Y+a.H,y+h);x=Math.Min(a.X,x);y=Math.Min(a.Y,y);w=right-x;h=bottom-y;}
        Dirty=(x,y,w,h); Generation++;
    }
    public bool Ready { get; private set; }
    public event Action<string>? ClipboardReceived;
    readonly Inflater zrle = new();
    bool disposed;
    public Task ConnectAsync(string host, int port, string user, string password, bool raw, CancellationToken token)
        => ConnectAsync(host, port, user, password, new ViewerOptions { Encoding = raw ? FrameEncoding.Raw : FrameEncoding.Zrle }, token);
    public async Task ConnectAsync(string host, int port, string user, string password, ViewerOptions settings, CancellationToken token)
    {
        options = settings.Normalize(); sixteenBit = options.ColorDepth == ColorDepth.Rgb565;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.ConnectTimeoutSeconds));
        using var registration = deadline.Token.Register(Dispose);
        try
        {
            await tcp.ConnectAsync(host, port, deadline.Token);
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, options.KeepAlive);
            if (options.KeepAlive)
            {
                tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
                tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 1);
            }
            stream = tcp.GetStream(); stream.ReadTimeout = options.ConnectTimeoutSeconds * 1000; stream.WriteTimeout = 15000;
            await Task.Run(() => Handshake(user, password), deadline.Token);
        }
        catch (Exception ex) when (deadline.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("连接或认证超时，请检查服务器地址或调整连接超时", ex);
        }
    }
    byte[] Read(int count)
    {
        if (count < 0 || count > 64 * 1024 * 1024) throw new IOException("服务器数据超出限制");
        byte[] data = new byte[count]; stream.ReadExactly(data); Interlocked.Add(ref ReceivedBytes,count); return data;
    }
    int Byte() => Read(1)[0];
    int U16() => BinaryPrimitives.ReadUInt16BigEndian(Read(2));
    int I32() => BinaryPrimitives.ReadInt32BigEndian(Read(4));
    void Send(byte[] data) { lock (sendGate) { if (!disposed) stream.Write(data); } }
    static void Put16(Span<byte> data, int value) => BinaryPrimitives.WriteUInt16BigEndian(data, checked((ushort)value));
    static void Put32(Span<byte> data, int value) => BinaryPrimitives.WriteInt32BigEndian(data, value);
    string Text(int max)
    {
        int n = I32(); if (n < 0 || n > max) throw new IOException("服务器文本超出限制");
        return Encoding.UTF8.GetString(Read(n));
    }
    void Handshake(string user, string password)
    {
        string version = Encoding.ASCII.GetString(Read(12));
        if (!version.StartsWith("RFB ") || !int.TryParse(version.AsSpan(8,3), out int minor)) throw new IOException("不是 VNC 服务器");
        int proto = minor >= 8 ? 8 : minor == 7 ? 7 : 3;
        Send(Encoding.ASCII.GetBytes($"RFB 003.00{proto}\n"));
        int[] offered;
        if (proto == 3) offered = [I32()];
        else { int count = Byte(); if (count == 0) throw new RfbAuthenticationException(Text(8192)); offered = Read(count).Select(b => (int)b).ToArray(); }
        int security = user.Length > 0 ? (offered.Contains(30) ? 30 : -1)
            : offered.Contains(2) && password.Length > 0 ? 2 : offered.Contains(1) ? 1 : offered.Contains(2) ? 2 : -1;
        if (security < 0) throw new RfbAuthenticationException("认证方式不支持：Mac 请填写系统账号，普通 VNC 请留空账号");
        if (proto != 3) Send([(byte)security]);
        if (security == 2)
        {
            byte[] key = new byte[8]; byte[] pw = Encoding.UTF8.GetBytes(password);
            for (int i=0; i<Math.Min(8,pw.Length); i++) { int b=pw[i], r=0; for(int j=0;j<8;j++){r=(r<<1)|(b&1);b>>=1;} key[i]=(byte)r; }
            using var des = DES.Create(); des.Key=key; des.Mode=CipherMode.ECB; des.Padding=PaddingMode.None;
            Send(des.CreateEncryptor().TransformFinalBlock(Read(16),0,16));
            CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(pw);
        }
        if (security == 30) AuthenticateMac(user, password);
        if (proto == 8 || security != 1) if (I32()!=0) throw new RfbAuthenticationException(proto==8 ? Text(8192) : "账号或密码错误");
        Send([(byte)(options.SharedSession ? 1 : 0)]); int w=U16(), h=U16(); Read(16); Text(65536); Resize(w,h);
        // 32bpp little endian true colour, BGRX. ZRLE uses compact BGR pixels.
        Send(sixteenBit ? [0,0,0,0,16,16,0,1,0,31,0,63,0,31,11,5,0,0,0,0]
            : [0,0,0,0,32,24,0,1,0,255,0,255,0,255,16,8,0,0,0,0]);
        var encodings = new List<int>();
        bool raw = options.Encoding == FrameEncoding.Raw;
        encodings.Add(raw ? 0 : 16);
        if (options.UseCopyRect) encodings.Add(1);
        encodings.Add(raw ? 16 : 0); encodings.Add(-223);
        int[] enc = encodings.ToArray();
        byte[] msg = new byte[4+4*enc.Length]; msg[0]=2; Put16(msg.AsSpan(2),enc.Length);
        for(int i=0;i<enc.Length;i++) Put32(msg.AsSpan(4+i*4),enc[i]);
        Send(msg); stream.ReadTimeout=Timeout.Infinite; Ready=true; WasConnected=true; Request(false);
    }
    static BigInteger Big(byte[] b) => new(b, isUnsigned:true, isBigEndian:true);
    static byte[] Fixed(BigInteger n, int length)
    {
        byte[] b=n.ToByteArray(isUnsigned:true,isBigEndian:true), result=new byte[length];
        if(b.Length>length) throw new IOException("无效的密钥长度"); b.CopyTo(result,length-b.Length); return result;
    }
    void AuthenticateMac(string user, string password)
    {
        int generator=U16(), length=U16();
        if(length<128 || length>1024) throw new IOException("不安全的 DH 密钥长度");
        byte[] modulus=Read(length); BigInteger prime=Big(modulus), peer=Big(Read(length));
        if(generator<2 || generator>=prime-1 || peer<=1 || peer>=prime-1 || !ProbablePrime(prime)) throw new IOException("无效的 DH 参数");
        BigInteger secret=Big(RandomNumberGenerator.GetBytes(length));
        BigInteger shared=BigInteger.ModPow(peer,secret,prime);
        if(shared<=1 || shared>=prime-1) throw new IOException("不安全的 DH 共享密钥");
        byte[] credentials=RandomNumberGenerator.GetBytes(128);
        void Pack(string value,int offset) { byte[] b=Encoding.UTF8.GetBytes(value); if(b.Length>63) throw new IOException("Mac 账号或密码超过 63 字节"); b.CopyTo(credentials,offset); credentials[offset+b.Length]=0; }
        Pack(user,0); Pack(password,64);
        using var aes=Aes.Create(); aes.Key=MD5.HashData(Fixed(shared,length));
        byte[] encrypted=aes.EncryptEcb(credentials,PaddingMode.None);
        Send([..encrypted,..Fixed(BigInteger.ModPow(generator,secret,prime),length)]);
        CryptographicOperations.ZeroMemory(credentials);
    }
    static bool ProbablePrime(BigInteger n)
    {
        if(n<3 || n.IsEven) return false;
        BigInteger d=n-1; int s=0; while(d.IsEven){d>>=1;s++;}
        for(int i=0;i<20;i++)
        {
            BigInteger a=2+Big(RandomNumberGenerator.GetBytes(n.GetByteCount(isUnsigned:true)))%(n-3);
            BigInteger x=BigInteger.ModPow(a,d,n); if(x==1 || x==n-1) continue;
            bool ok=false; for(int j=1;j<s;j++){x=x*x%n;if(x==n-1){ok=true;break;}}
            if(!ok) return false;
        }
        return true;
    }
    void Resize(int w,int h)
    {
        if(w<=0 || h<=0 || w>16384 || h>16384 || (long)w*h>33554432) throw new IOException("远端分辨率超出限制");
        Dirty=null;Width=w;Height=h;Pixels=new byte[checked(w*h*4)];
    }
    void Rect(int x,int y,int w,int h)
    { if(w<=0||h<=0||x<0||y<0||x+w>Width||y+h>Height) throw new IOException("无效的画面区域"); }
    public void Run(CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            int type=Byte();
            if(type==0)
            {
                var updateClock=System.Diagnostics.Stopwatch.StartNew();
                Byte(); int count=U16();
                // Lock per rectangle; the UI never blocks on an entire update's network read.
                for(int i=0;i<count;i++)
                {
                    int x=U16(),y=U16(),w=U16(),h=U16(),encoding=I32();
                    if(encoding==-223){lock(Gate){Resize(w,h);Mark(0,0,w,h);}continue;}
                    Rect(x,y,w,h);
                    if(encoding==0)
                    {
                        ActiveEncoding = "Raw";
                        byte[] data=Read(checked(w*h*(sixteenBit ? 2 : 4)));
                        if (sixteenBit)
                        {
                            byte[] expanded = new byte[w*h*4];
                            for (int pixel=0;pixel<w*h;pixel++)
                            {
                                int c = Expand565(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pixel*2)));
                                expanded[pixel*4]=(byte)c; expanded[pixel*4+1]=(byte)(c>>8); expanded[pixel*4+2]=(byte)(c>>16);
                            }
                            data = expanded;
                        }
                        lock(Gate){for(int row=0;row<h;row++) Buffer.BlockCopy(data,row*w*4,Pixels,((y+row)*Width+x)*4,w*4);Mark(x,y,w,h);}
                    }
                    else if(encoding==1)
                    {
                        ActiveEncoding = "CopyRect";
                        int sx=U16(),sy=U16();Rect(sx,sy,w,h);
                        lock(Gate){for(int row=0;row<h;row++){int r=y>sy?h-1-row:row;Buffer.BlockCopy(Pixels,((sy+r)*Width+sx)*4,Pixels,((y+r)*Width+x)*4,w*4);}Mark(x,y,w,h);}
                    }
                    else if(encoding==16)
                    {
                        ActiveEncoding = "ZRLE";
                        int size=I32();byte[] compressed=Read(size);
                        byte[] decoded=DecodeZrle(compressed,w,h);
                        lock(Gate){for(int row=0;row<h;row++)Buffer.BlockCopy(decoded,row*w*4,Pixels,((y+row)*Width+x)*4,w*4);Mark(x,y,w,h);}
                    }
                    else throw new IOException($"不支持的画面编码：{encoding}");
                }
                Updates++;
                int delay=(int)Math.Ceiling(1000.0/options.MaxFps)-(int)updateClock.ElapsedMilliseconds;
                if(delay>0 && token.WaitHandle.WaitOne(delay))return;
                Request(Interlocked.Exchange(ref forceRefresh, 0) == 0);
            }
            else if(type==1){Read(3);Read(checked(U16()*6));}
            else if(type==2) { BellReceived?.Invoke(); }
            else if(type==3){Read(3);string text=Text(1024*1024);ClipboardReceived?.Invoke(text);}
            else throw new IOException($"不支持的服务器消息：{type}");
        }
    }
    byte[] DecodeZrle(byte[] compressed,int w,int h)
    {
        zrle.SetInput(compressed);
        // Incremental inflater is retained across rectangles, as required by RFB.
        byte[] scratch=new byte[8192]; int cursor=0, available=0;
        int Next(){if(cursor==available){available=zrle.Inflate(scratch);cursor=0;if(available==0)throw new IOException("ZRLE 数据不完整");}return scratch[cursor++];}
        int Color()=>sixteenBit ? Expand565(Next()|(Next()<<8)) : Next()|(Next()<<8)|(Next()<<16);
        byte[] output=new byte[w*h*4];
        for(int ty=0;ty<h;ty+=64)for(int tx=0;tx<w;tx+=64)
        {
            int tw=Math.Min(64,w-tx),th=Math.Min(64,h-ty),total=tw*th,pos=0;
            int mode=Next(),colors=mode&127; bool rle=(mode&128)!=0;
            if(mode==129 || (!rle && colors>16))throw new IOException("非法 ZRLE 子编码");
            int[] palette=new int[colors];for(int p=0;p<colors;p++)palette[p]=Color();
            void Pixel(int c){if(pos>=total)throw new IOException("ZRLE 越界");int at=((ty+pos/tw)*w+tx+pos%tw)*4;output[at]=(byte)c;output[at+1]=(byte)(c>>8);output[at+2]=(byte)(c>>16);pos++;}
            int Index(int index){if(index>=colors)throw new IOException("ZRLE 调色板越界");return palette[index];}
            if(mode==0){while(pos<total)Pixel(Color());}
            else if(mode==1){while(pos<total)Pixel(palette[0]);}
            else if(!rle)
            {
                int bits=colors<=2?1:colors<=4?2:4;
                for(int row=0;row<th;row++){int packed=0,left=0;for(int col=0;col<tw;col++){if(left==0){packed=Next();left=8;}left-=bits;Pixel(Index((packed>>left)&((1<<bits)-1)));}}
            }
            else while(pos<total)
            {
                int index=colors==0?0:Next();int c=colors==0?Color():Index(index&127),run=1;
                if(colors==0 || (index&128)!=0){int b;do{b=Next();run+=b;if(run>total-pos)throw new IOException("ZRLE 游程越界");}while(b==255);}
                for(int k=0;k<run;k++)Pixel(c);
            }
        }
        byte[] extra=new byte[1];if(cursor!=available || zrle.Inflate(extra)!=0 || !zrle.IsNeedingInput)throw new IOException("ZRLE 多余数据");
        return output;
    }
    static int Expand565(int value)
    {
        int red=(value>>11)&31, green=(value>>5)&63, blue=value&31;
        return ((red*255/31)<<16)|((green*255/63)<<8)|(blue*255/31);
    }
    void Request(bool incremental){byte[] m=new byte[10];m[0]=3;m[1]=(byte)(incremental?1:0);Put16(m.AsSpan(6),Width);Put16(m.AsSpan(8),Height);Send(m);}
    public void Key(int key,bool down){if(!Ready || (options.ViewOnly && down))return;byte[] m=new byte[8];m[0]=4;m[1]=(byte)(down?1:0);Put32(m.AsSpan(4),key);Send(m);}
    public void Pointer(int x,int y,int buttons){if(!Ready || options.ViewOnly)return;SendPointerPacket(x,y,buttons);}
    public void ReleasePointer(int x,int y){if(Ready)SendPointerPacket(x,y,0);}
    void SendPointerPacket(int x,int y,int buttons){byte[] m=new byte[6];m[0]=5;m[1]=(byte)buttons;Put16(m.AsSpan(2),Math.Clamp(x,0,Width-1));Put16(m.AsSpan(4),Math.Clamp(y,0,Height-1));Send(m);}
    public void Clipboard(string text){if(!Ready || !options.CanSendClipboard)return;byte[] b=Encoding.UTF8.GetBytes(text);if(b.Length>1024*1024)return;byte[] m=new byte[8+b.Length];m[0]=6;Put32(m.AsSpan(4),b.Length);b.CopyTo(m,8);Send(m);}
    public void Dispose(){Ready=false;disposed=true;tcp.Dispose();}
}
