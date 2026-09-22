using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Diagnostics;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class RuntimeToggleTests
{
    [TestMethod]
    public void FullSequenceCapturesOnlyAfterItsRequestedModeWasObserved()
    {
        var sequence = new RuntimeToggleSequence(0, .2, 2, 10);
        Assert.AreEqual(default(RuntimeToggleStep), sequence.Advance(.1, false));
        Assert.AreEqual(default(RuntimeToggleStep), sequence.Advance(2, true));
        var first = sequence.Advance(2.2, true);
        Assert.AreEqual(0, first.SetMode); Assert.IsNull(first.Capture); Assert.IsFalse(first.Complete);
        double now = 2.2;
        foreach (var expected in new[] { ("native-before",(int?)1), ("enabled",(int?)2), ("coverage",(int?)0), ("native-after",(int?)null) })
        {
            Assert.AreEqual(default(RuntimeToggleStep), sequence.Advance(now += .3, false));
            Assert.AreEqual(default(RuntimeToggleStep), sequence.Advance(now += .3, true));
            var step = sequence.Advance(now += .3, true);
            Assert.AreEqual(expected.Item1, step.Capture); Assert.AreEqual(expected.Item2, step.SetMode);
        }
        Assert.IsTrue(sequence.Complete); Assert.IsTrue(sequence.Advance(20, false).Complete);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => sequence.Advance(19, true));
    }
    [TestMethod]
    public void UnreadyModesCannotPassThroughTheDeadlineOrProduceFakeCaptures()
    {
        var sequence = new RuntimeToggleSequence(0, .1, 1, 3);
        Assert.IsNull(sequence.Advance(2, false).Capture);
        Assert.IsNull(sequence.Advance(3, false).Capture);
        Assert.ThrowsException<TimeoutException>(() => sequence.Advance(3.1, false));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => sequence.Advance(double.NaN, true));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new RuntimeToggleSequence(double.NaN));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new RuntimeToggleSequence(0, -.1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new RuntimeToggleSequence(0, .1, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new RuntimeToggleSequence(0, 2, 1, 1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new RuntimeToggleSequence(0, double.PositiveInfinity));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new RuntimeToggleSequence(0, 0, 1, double.NaN));
    }
    private static byte[] Image(byte r, byte g = 0, byte b = 0, byte a = 255)
    {
        var data = new byte[400];
        for (int i = 0; i < data.Length; i += 4) {data[i]=r;data[i+1]=g;data[i+2]=b;data[i+3]=a;}
        return data;
    }
    [TestMethod]
    public void ActualChangeAndRestoredNativeImageAreRequiredNotJustModeTokens()
    {
        var native = Image(20, 30, 40); var on = Image(60, 45, 40);
        var good = RuntimeToggleAnalysis.Compare(native, on, native);
        Assert.AreEqual("PASS", good.Status); Assert.AreEqual(1, good.ChangedPixelFraction);
        Assert.AreEqual(55 / (3.0*255), good.EffectMeanAbsoluteError, 1e-12);
        Assert.AreEqual("INCONCLUSIVE",RuntimeToggleAnalysis.Compare(native,native,native).Status);
        Assert.AreEqual("INCONCLUSIVE",RuntimeToggleAnalysis.Compare(native,on,Image(45,55,65)).Status);
        Assert.AreEqual("INCONCLUSIVE",RuntimeToggleAnalysis.Compare(native,Image(20,30,40,0),native).Status);
        var weak = (byte[])native.Clone(); weak[0]++;
        Assert.AreEqual("INCONCLUSIVE",RuntimeToggleAnalysis.Compare(native,weak,native).Status);
        Assert.ThrowsException<ArgumentException>(()=>RuntimeToggleAnalysis.Compare([],[],[]));
        Assert.ThrowsException<ArgumentException>(()=>RuntimeToggleAnalysis.Compare([1],[1],[1]));
        Assert.ThrowsException<ArgumentException>(()=>RuntimeToggleAnalysis.Compare(native,[],native));
        Assert.ThrowsException<ArgumentException>(()=>RuntimeToggleAnalysis.Compare(native,native,[]));
    }
    [TestMethod]
    public void PngRetainsExactRgbaAndCorrectlyFlipsTheOpenGlRows()
    {
        byte[] bottomUp = [255,0,0,255, 0,255,0,255, 0,0,255,255, 70,80,90,100];
        foreach (bool flip in new[]{false,true})
        {
            using var output = new MemoryStream(); RuntimePng.Write(output,2,2,bottomUp,flip);
            var bytes=output.ToArray(); CollectionAssert.AreEqual(new byte[]{137,80,78,71,13,10,26,10},bytes[..8]);
            int at=8; var raw=new MemoryStream(); bool end=false;
            while(at<bytes.Length)
            {
                int length=BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(at));
                string type=Encoding.ASCII.GetString(bytes,at+4,4);
                if(type=="IHDR") {Assert.AreEqual(2,BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(at+8))); Assert.AreEqual(6,bytes[at+17]);}
                if(type=="IDAT")
                {
                    using var z=new ZLibStream(new MemoryStream(bytes,at+8,length),CompressionMode.Decompress);
                    z.CopyTo(raw);
                }
                if(type=="IEND") {end=true; CollectionAssert.AreEqual(new byte[]{0xae,0x42,0x60,0x82},bytes[(at+8)..(at+12)]);}
                at+=12+length;
            }
            Assert.IsTrue(end); byte[] decoded=raw.ToArray(); Assert.AreEqual(18,decoded.Length);
            Assert.AreEqual(0,decoded[0]);Assert.AreEqual(0,decoded[9]);
            CollectionAssert.AreEqual(flip?bottomUp[8..]:bottomUp[..8],decoded[1..9]);
            CollectionAssert.AreEqual(flip?bottomUp[..8]:bottomUp[8..],decoded[10..18]);
            string? directory=Environment.GetEnvironmentVariable("VINTAGERTX_PNG_TEST");
            if(directory is not null) {Directory.CreateDirectory(directory);File.WriteAllBytes(Path.Combine(directory,$"rgba-{flip}.png"),bytes);}
        }
        Assert.ThrowsException<ArgumentNullException>(()=>RuntimePng.Write(null!,1,1,[0,0,0,0]));
        Assert.ThrowsException<ArgumentOutOfRangeException>(()=>RuntimePng.Write(new MemoryStream(),0,1,[]));
        Assert.ThrowsException<ArgumentOutOfRangeException>(()=>RuntimePng.Write(new MemoryStream(),1,-1,[]));
        Assert.ThrowsException<ArgumentOutOfRangeException>(()=>RuntimePng.Write(new MemoryStream(),int.MaxValue,2,[]));
        Assert.ThrowsException<ArgumentOutOfRangeException>(()=>RuntimePng.Write(new MemoryStream(),1,1,[]));
    }
}
