Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing.Common,System.Drawing.Primitives,System.Private.Windows.GdiPlus,System.Private.Windows.Core,System.Collections,System.Runtime -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Drawing;
public static class CoffeeCutout {
  public static void Save(string source, string destination, int threshold) {
    using var input = new Bitmap(source);
    using var output = new Bitmap(input.Width, input.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    int w=input.Width, h=input.Height;
    var mask=new bool[w*h];
    var seen=new bool[w*h];
    var queue=new Queue<int>();
    Action<int,int> visit=(x,y)=> {
      if(x<0 || y<0 || x>=w || y>=h) return;
      int i=y*w+x;
      if(seen[i]) return;
      seen[i]=true;
      var c=input.GetPixel(x,y);
      int lo=Math.Min(c.R,Math.Min(c.G,c.B));
      int hi=Math.Max(c.R,Math.Max(c.G,c.B));
      if(lo>=threshold && hi-lo<=22) {mask[i]=true;queue.Enqueue(i);}
    };
    for(int x=0;x<w;x++){visit(x,0);visit(x,h-1);}
    for(int y=0;y<h;y++){visit(0,y);visit(w-1,y);}
    if(source.Contains("latte-pinterest")) { visit(590,460); visit(580,480); visit(600,470); }
    while(queue.Count>0) {int i=queue.Dequeue(),x=i%w,y=i/w; visit(x-1,y);visit(x+1,y);visit(x,y-1);visit(x,y+1);}
    for(int y=0;y<h;y++) for(int x=0;x<w;x++) {
      var c=input.GetPixel(x,y);
      output.SetPixel(x,y,mask[y*w+x]?Color.Transparent:c);
    }
    int minX=w,minY=h,maxX=0,maxY=0;
    for(int y=0;y<h;y++) for(int x=0;x<w;x++) if(!mask[y*w+x]){minX=Math.Min(minX,x);minY=Math.Min(minY,y);maxX=Math.Max(maxX,x);maxY=Math.Max(maxY,y);}
    using var cropped = new Bitmap(maxX-minX+33,maxY-minY+33,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    using(var graphics=Graphics.FromImage(cropped)) graphics.DrawImage(output,16-minX,16-minY);
    cropped.Save(destination,System.Drawing.Imaging.ImageFormat.Png);
  }
}
"@
$project = Split-Path -Parent $PSScriptRoot
[CoffeeCutout]::Save((Join-Path $project 'assets\sources\iced-coffee-pinterest.jpg'),(Join-Path $project 'assets\images\iced_coffee.png'),220)
[CoffeeCutout]::Save((Join-Path $project 'assets\sources\latte-pinterest.jpg'),(Join-Path $project 'assets\images\coffee_cup.png'),180)
[CoffeeCutout]::Save((Join-Path $project 'assets\sources\espresso-pinterest.jpg'),(Join-Path $project 'assets\images\espresso.png'),230)



