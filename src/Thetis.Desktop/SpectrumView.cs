using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Thetis.Engine;
using Thetis.Preview;

namespace Thetis.Desktop;

public sealed class SpectrumView : Control, IDisposable
{
    private const int Columns = 1024, Rows = 160;
    private readonly float[] levels = new float[Columns];
    private readonly int[] waterfall = new int[Columns*Rows];
    private readonly WriteableBitmap image = new(new PixelSize(Columns,Rows),new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Opaque);
    private readonly Pen grid = new(new SolidColorBrush(Color.Parse("#203044")),1);
    private readonly Pen line = new(new SolidColorBrush(Color.Parse("#45E3CE")),1.2);
    private readonly IBrush text = new SolidColorBrush(Color.Parse("#8196AC"));
    private ReceiveSpectrumFrame? frame;
    private ReceiveSpectrumFrame? renderedFrame;
    public long FramesDisplayed { get; private set; }
    public long FramesRendered { get; private set; }
    private long lifetimeDisplayed,lifetimeRendered,sourceSkipped;
    public DisplayTelemetry Telemetry => new(lifetimeDisplayed,lifetimeRendered,sourceSkipped,Bounds.Width,Bounds.Height);
    public void Update(ReceiveSpectrumFrame? value)
    {
        if (value is null || frame?.Sequence == value.Sequence && frame?.TuningGeneration == value.TuningGeneration) return;
        if (frame is null || frame.TuningGeneration != value.TuningGeneration || frame.SampleRate != value.SampleRate)
            Array.Fill(waterfall,unchecked((int)0xff0a111b));
        if (frame is not null && frame.TuningGeneration == value.TuningGeneration)
            sourceSkipped += Math.Max(0,value.Sequence-frame.Sequence-1);
        frame = value; ++FramesDisplayed; ++lifetimeDisplayed;
        var source = value.LevelsDb.Span;
        Buffer.BlockCopy(waterfall,0,waterfall,Columns*sizeof(int),Columns*(Rows-1)*sizeof(int));
        for (int x = 0; x < Columns; ++x)
        {
            int start = x*source.Length/Columns, end = (x+1)*source.Length/Columns;
            float peak = -200;
            for (int i = start; i < end; ++i) peak = Math.Max(peak,source[i]);
            levels[x] = peak; waterfall[x] = ColorFor(peak);
        }
        using (var locked = image.Lock())
            for (int y = 0; y < Rows; ++y) Marshal.Copy(waterfall,y*Columns,locked.Address+y*locked.RowBytes,Columns);
        InvalidateVisual();
    }
    public void Reset()
    {
        frame = renderedFrame = null; FramesDisplayed = FramesRendered = 0; Array.Clear(waterfall); InvalidateVisual();
    }
    internal static int ColorFor(double db)
    {
        double v = Math.Clamp((db+110)/105,0,1);
        int r, g, b;
        if (v < .5) { double t = v*2; r = (int)(10+10*t); g = (int)(17+95*t); b = (int)(27+130*t); }
        else { double t = (v-.5)*2; r = (int)(20+225*t); g = (int)(112+119*t); b = (int)(157-73*t); }
        return unchecked((int)0xff000000) | r<<16 | g<<8 | b;
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double width = Math.Max(1,Bounds.Width-68), height = Math.Max(70,(Bounds.Height-78)*.55);
        var plot = new Rect(48,32,width,height);
        Label(context,"SPECTRUM · PRE-DEMOD · UNCALIBRATED dB",new Point(16,10),10);
        for (int i = 0; i <= 5; ++i)
        {
            double y = plot.Y+i*height/5;
            context.DrawLine(grid,new(plot.X,y),new(plot.Right,y));
            Label(context,(-20*i).ToString(CultureInfo.InvariantCulture),new Point(12,y-6),10);
        }
        for (int i = 0; i <= 4; ++i)
        {
            double x = plot.X+i*width/4;
            context.DrawLine(grid,new(x,plot.Y),new(x,plot.Bottom));
            if (frame is not null)
            {
                double hz = frame.FirstFrequencyHz+(frame.LevelsDb.Length-1)*frame.BinWidthHz*i/4;
                Label(context,(hz/1e6).ToString("F3",CultureInfo.InvariantCulture),new Point(x-22,plot.Bottom+7),10);
            }
        }
        var waterfallRect = new Rect(plot.X,plot.Bottom+34,width,Math.Max(1,Bounds.Height-plot.Bottom-48));
        if (frame is not null)
        {
            if (!ReferenceEquals(frame,renderedFrame)) { renderedFrame = frame; ++FramesRendered; ++lifetimeRendered; }
            using (context.PushClip(plot))
            {
                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                {
                    path.BeginFigure(new(plot.X,plot.Bottom-Math.Clamp((levels[0]+100)/100,0,1)*height),false);
                    for (int x = 1; x < Columns; ++x) path.LineTo(new(plot.X+x*width/(Columns-1),plot.Bottom-Math.Clamp((levels[x]+100)/100,0,1)*height));
                    path.EndFigure(false);
                }
                context.DrawGeometry(null,line,geometry);
            }
            context.DrawImage(image,new Rect(0,0,Columns,Rows),waterfallRect);
        }
        else Label(context,"Connect a simulator to begin",new Point(plot.X+20,plot.Y+height/2),14);
        context.DrawRectangle(null,grid,waterfallRect);
    }
    private void Label(DrawingContext context,string value,Point at,double size) => context.DrawText(
        new FormattedText(value,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(FontFamily.Default),size,text),at);
    public void Dispose() => image.Dispose();
}
