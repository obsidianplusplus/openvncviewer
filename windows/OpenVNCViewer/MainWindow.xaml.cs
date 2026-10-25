// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace OpenVNCViewer;
public partial class MainWindow : Window
{
    RfbClient? client;
    CancellationTokenSource? cancel;
    Channel<Action<RfbClient>>? input;
    WriteableBitmap? bitmap;
    long generation=-1, frames, lastFrames;
    readonly Stopwatch clock=Stopwatch.StartNew();
    double lastPaint,lastStats,lastClipboard;
    long lastBytes;
    string clipboardEcho="";
    int buttons;
    Point? pendingPointer;
    readonly HashSet<int> pressed=[];
    bool closing;
    public MainWindow()
    {
        InitializeComponent();CompositionTarget.Rendering+=Render;
        Closing+=(_,_)=>{closing=true;Stop();CompositionTarget.Rendering-=Render;};
    }
    async void Connect(object sender,RoutedEventArgs e)
    {
        Stop();
        if(string.IsNullOrWhiteSpace(Host.Text)||!int.TryParse(Port.Text,out int port)||port<1||port>65535){Status.Text="请输入有效的服务器地址和端口";return;}
        var current=new RfbClient();var cts=new CancellationTokenSource();client=current;cancel=cts;
        input=Channel.CreateBounded<Action<RfbClient>>(new BoundedChannelOptions(256){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
        var queue=input;ConnectButton.IsEnabled=false;Status.Text="正在连接并认证…";
        string host=Host.Text.Trim(),user=User.Text,password=Password.Password; bool raw=false;
        current.ClipboardReceived+=text=>Dispatcher.BeginInvoke(new Action(()=>
        {
            if(client!=current||ShareClipboard.IsChecked!=true)return;
            try{Clipboard.SetText(text);clipboardEcho=text;}catch(COMException){}
        }));
        try
        {
            await current.ConnectAsync(host,port,user,password,raw,cts.Token);
            if(client!=current)return;
            Password.Clear();Surface.Focus();Status.Text="已连接";
            var writer=Task.Run(async()=>{await foreach(var action in queue.Reader.ReadAllAsync(cts.Token))action(current);},cts.Token);
            var reader=Task.Run(()=>current.Run(cts.Token),cts.Token);
            await Task.WhenAny(reader,writer);
            current.Dispose();cts.Cancel();
            try{await Task.WhenAll(reader,writer);}catch(Exception ex){if(client==current&&!closing)Status.Text="连接已结束："+ex.Message;}
        }
        catch(Exception ex){if(client==current&&!closing)Status.Text="连接失败："+ex.Message;}
        finally{if(client==current){Stop();ConnectButton.IsEnabled=true;}}
    }
    void Stop()
    {
        cancel?.Cancel();input?.Writer.TryComplete();client?.Dispose();client=null;cancel=null;input=null;
        pressed.Clear();buttons=0;pendingPointer=null;bitmap=null;Desktop.Source=null;generation=-1;frames=0;lastFrames=0;lastBytes=0;lastStats=clock.Elapsed.TotalSeconds;
        if(!closing)ConnectButton.IsEnabled=true;
    }
    void Disconnect(object sender,RoutedEventArgs e){Stop();Status.Text="已断开连接";}
    void Enqueue(Action<RfbClient> action)
    {
        if(client?.Ready!=true)return;
        if(input?.Writer.TryWrite(action)!=true){Stop();Status.Text="输入队列拥堵，连接已停止，请检查网络";}
    }
    void Render(object? sender,EventArgs e)
    {
        var current=client;if(current?.Ready!=true)return;
        double now=clock.Elapsed.TotalSeconds;
        if(now-lastPaint<1.0/60)return;lastPaint=now;
        if(pendingPointer is Point point){pendingPointer=null;int mask=buttons;Enqueue(c=>c.Pointer((int)point.X,(int)point.Y,mask));}
        if(Monitor.TryEnter(current.Gate))
        {
            try
            {
                if(generation!=current.Generation)
                {
                    if(bitmap==null||bitmap.PixelWidth!=current.Width||bitmap.PixelHeight!=current.Height)
                    {bitmap=new WriteableBitmap(current.Width,current.Height,96,96,PixelFormats.Bgr32,null);Desktop.Source=bitmap;}
                    if(current.Dirty is {} dirty)
                    {
                        bitmap.WritePixels(new Int32Rect(dirty.X,dirty.Y,dirty.W,dirty.H),current.Pixels,current.Width*4,(dirty.Y*current.Width+dirty.X)*4);
                        current.Dirty=null;
                    }
                    generation=current.Generation;frames++;
                }
            }
            finally{Monitor.Exit(current.Gate);}
        }
        if(now-lastStats>=1)
        {Status.Text=$"已连接 · {current.Width} × {current.Height} · 接收 {(current.ReceivedBytes-lastBytes)/(now-lastStats)/1048576:F1} MB/s · 实际绘制 {(frames-lastFrames)/(now-lastStats):F0} 帧/秒 · F11 全屏";lastFrames=frames;lastBytes=current.ReceivedBytes;lastStats=now;}
        if(now-lastClipboard>.5)
        {
            lastClipboard=now;
            if(ShareClipboard.IsChecked==true&&Surface.IsKeyboardFocusWithin&&IsActive)
            {
                try{if(Clipboard.ContainsText()){string text=Clipboard.GetText();if(text!=clipboardEcho){clipboardEcho=text;Enqueue(c=>c.Clipboard(text));}}}catch(COMException){}
            }
        }
    }
    Point? RemotePoint(MouseEventArgs e)
    {
        var c=client;if(c?.Ready!=true)return null;
        double scale=Math.Min(Surface.ActualWidth/c.Width,Surface.ActualHeight/c.Height);
        if(scale<=0)return null;
        Point p=e.GetPosition(Surface);double x=(p.X-(Surface.ActualWidth-c.Width*scale)/2)/scale,y=(p.Y-(Surface.ActualHeight-c.Height*scale)/2)/scale;
        return new Point(Math.Clamp(x,0,c.Width-1),Math.Clamp(y,0,c.Height-1));
    }
    void PointerMove(object s,MouseEventArgs e)=>pendingPointer=RemotePoint(e);
    static int Button(MouseButton b)=>b==MouseButton.Left?1:b==MouseButton.Middle?2:b==MouseButton.Right?4:0;
    void PointerDown(object s,MouseButtonEventArgs e){Surface.Focus();Surface.CaptureMouse();buttons|=Button(e.ChangedButton);SendPointer(e);e.Handled=true;}
    void PointerUp(object s,MouseButtonEventArgs e){buttons&=~Button(e.ChangedButton);SendPointer(e);if(buttons==0)Surface.ReleaseMouseCapture();e.Handled=true;}
    void SendPointer(MouseEventArgs e){pendingPointer=null;if(RemotePoint(e)is Point p){int mask=buttons;Enqueue(c=>c.Pointer((int)p.X,(int)p.Y,mask));}}
    void Wheel(object s,MouseWheelEventArgs e){if(RemotePoint(e)is Point p){int mask=buttons,wheel=e.Delta>0?8:16;Enqueue(c=>{c.Pointer((int)p.X,(int)p.Y,mask|wheel);c.Pointer((int)p.X,(int)p.Y,mask);});}e.Handled=true;}
    void KeyDownRemote(object s,KeyEventArgs e)
    {
        Key key=e.Key==System.Windows.Input.Key.System?e.SystemKey:e.Key;
        if(key==System.Windows.Input.Key.F11){WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;WindowStyle=WindowStyle==WindowStyle.None?WindowStyle.SingleBorderWindow:WindowStyle.None;e.Handled=true;return;}
        int sym=Keysym(key);if(sym!=0){pressed.Add(sym);Enqueue(c=>c.Key(sym,true));}e.Handled=true;
    }
    void KeyUpRemote(object s,KeyEventArgs e){int sym=Keysym(e.Key==System.Windows.Input.Key.System?e.SystemKey:e.Key);if(sym!=0){pressed.Remove(sym);Enqueue(c=>c.Key(sym,false));}e.Handled=true;}
    void ReleaseKeys(object s,KeyboardFocusChangedEventArgs e){foreach(int sym in pressed)Enqueue(c=>c.Key(sym,false));pressed.Clear();buttons=0;Enqueue(c=>c.Pointer(0,0,0));}
    int Keysym(Key key)
    {
        int special=key switch{System.Windows.Input.Key.Back=>0xff08,System.Windows.Input.Key.Tab=>0xff09,System.Windows.Input.Key.Return=>0xff0d,System.Windows.Input.Key.Escape=>0xff1b,System.Windows.Input.Key.Delete=>0xffff,System.Windows.Input.Key.Insert=>0xff63,System.Windows.Input.Key.Home=>0xff50,System.Windows.Input.Key.End=>0xff57,System.Windows.Input.Key.Left=>0xff51,System.Windows.Input.Key.Up=>0xff52,System.Windows.Input.Key.Right=>0xff53,System.Windows.Input.Key.Down=>0xff54,System.Windows.Input.Key.PageUp=>0xff55,System.Windows.Input.Key.PageDown=>0xff56,System.Windows.Input.Key.LeftShift=>0xffe1,System.Windows.Input.Key.RightShift=>0xffe2,System.Windows.Input.Key.LeftCtrl=>0xffe3,System.Windows.Input.Key.RightCtrl=>0xffe4,System.Windows.Input.Key.LeftAlt=>MacKeys.IsChecked==true?0xffeb:0xffe9,System.Windows.Input.Key.RightAlt=>MacKeys.IsChecked==true?0xffec:0xffea,System.Windows.Input.Key.LWin=>0xffeb,System.Windows.Input.Key.RWin=>0xffec,_=>0};
        if(special!=0)return special;
        if((int)key>=(int)System.Windows.Input.Key.F1&&(int)key<=(int)System.Windows.Input.Key.F24)return 0xffbe+(int)key-(int)System.Windows.Input.Key.F1;
        byte[] state=new byte[256];GetKeyboardState(state);var text=new System.Text.StringBuilder(8);
        int vk=KeyInterop.VirtualKeyFromKey(key);int count=ToUnicode((uint)vk,MapVirtualKey((uint)vk,0),state,text,text.Capacity,4);
        return count>0?text[0]<=255?text[0]:0x01000000|text[0]:0;
    }
    [DllImport("user32.dll")]static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")]static extern uint MapVirtualKey(uint code,uint type);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int ToUnicode(uint vk,uint scan,byte[] state,System.Text.StringBuilder buffer,int size,uint flags);
}
