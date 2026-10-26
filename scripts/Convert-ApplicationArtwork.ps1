[CmdletBinding()]
param([string]$Source = "$PSScriptRoot\..\artwork\FluffHistory.png", [string]$Destination = "$PSScriptRoot\..\src\FluffHistory.Desktop\Assets")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;using System.IO;using System.Drawing;using System.Drawing.Drawing2D;using System.Drawing.Imaging;
public static class ApplicationArtwork {
 static Bitmap Resize(Image source,int size) {
  var result=new Bitmap(size,size,PixelFormat.Format32bppArgb);
  using(var g=Graphics.FromImage(result))using(var attributes=new ImageAttributes()) {
   g.CompositingMode=CompositingMode.SourceCopy;g.InterpolationMode=InterpolationMode.HighQualityBicubic;
   g.PixelOffsetMode=PixelOffsetMode.HighQuality;attributes.SetWrapMode(WrapMode.TileFlipXY);
   var scale=Math.Min((double)size/source.Width,(double)size/source.Height);
   var width=Math.Max(1,(int)Math.Round(source.Width*scale));var height=Math.Max(1,(int)Math.Round(source.Height*scale));
   g.DrawImage(source,new Rectangle((size-width)/2,(size-height)/2,width,height),0,0,source.Width,source.Height,GraphicsUnit.Pixel,attributes);
  }return result;
 }
 public static void Png(Image source,int size,string path) {using(var bitmap=Resize(source,size))bitmap.Save(path,ImageFormat.Png);}
 static byte[] Frame(Image source,int size) {
  using(var bitmap=Resize(source,size))using(var memory=new MemoryStream())using(var w=new BinaryWriter(memory)) {
   if(size==256){bitmap.Save(memory,ImageFormat.Png);return memory.ToArray();}
   var maskStride=((size+31)/32)*4;
   w.Write(40);w.Write(size);w.Write(size*2);w.Write((ushort)1);w.Write((ushort)32);w.Write(0);w.Write(size*size*4);w.Write(0);w.Write(0);w.Write(0);w.Write(0);
   for(var y=size-1;y>=0;y--)for(var x=0;x<size;x++){var c=bitmap.GetPixel(x,y);w.Write(c.B);w.Write(c.G);w.Write(c.R);w.Write(c.A);}
   for(var y=size-1;y>=0;y--){var row=new byte[maskStride];for(var x=0;x<size;x++)if(bitmap.GetPixel(x,y).A==0)row[x/8]|=(byte)(128>>(x%8));w.Write(row);}
   return memory.ToArray();
  }
 }
 public static void Icon(Image source,string path) {
  var sizes=new[]{16,20,24,32,40,48,64,96,128,256};var frames=new byte[sizes.Length][];
  for(var i=0;i<sizes.Length;i++)frames[i]=Frame(source,sizes[i]);
  using(var w=new BinaryWriter(File.Create(path))) {
   w.Write((ushort)0);w.Write((ushort)1);w.Write((ushort)sizes.Length);var offset=6+16*sizes.Length;
   for(var i=0;i<sizes.Length;i++){w.Write((byte)(sizes[i]==256?0:sizes[i]));w.Write((byte)(sizes[i]==256?0:sizes[i]));w.Write((ushort)0);w.Write((ushort)1);w.Write((ushort)32);w.Write(frames[i].Length);w.Write(offset);offset+=frames[i].Length;}
   foreach(var frame in frames)w.Write(frame);
  }
 }
}
'@
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$image=[Drawing.Image]::FromFile([IO.Path]::GetFullPath($Source))
try {
 [ApplicationArtwork]::Icon($image,(Join-Path $Destination 'FluffHistory.ico'))
 foreach($asset in @(@{Name='StoreLogo';Size=50},@{Name='Square44x44Logo';Size=44},@{Name='Square150x150Logo';Size=150})) {
  [ApplicationArtwork]::Png($image,$asset.Size,(Join-Path $Destination ($asset.Name+'.png')))
  foreach($scale in @(125,150,200,400)){[ApplicationArtwork]::Png($image,[int]($asset.Size*$scale/100),(Join-Path $Destination ($asset.Name+'.scale-'+$scale+'.png')))}
 }
 foreach($size in @(16,20,24,32,40,48,64,96,128,256)) {
  [ApplicationArtwork]::Png($image,$size,(Join-Path $Destination ('Square44x44Logo.targetsize-'+$size+'_altform-unplated.png')))
 }
} finally { $image.Dispose() }
