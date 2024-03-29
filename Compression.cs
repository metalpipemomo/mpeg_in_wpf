﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.IO;
using Line = System.Windows.Shapes.Line;

namespace ImageCompression
{
    class Compression
    {
        public Compression(Image img)
        {

            //Construct stuff from source
            Debug.WriteLine($"Constructing Data...");
            var src = img.Source as BitmapSource ?? throw new ArgumentNullException("Image does not exist");
            var image = ConvertData(src);
            //Bytes to RGB
            var rgbpixels = RGB.MatrixBufferToRGB(image);
            //RGB to YCbCr
            var ycbcrpixels = YCBCR.RGBMatrixToYCBCRMatrix(rgbpixels);
            //Seperate channels
            YCBCR.DeconstructYCBCR(ycbcrpixels, out float[,] y, out float[,] cb, out float[,] cr);
            //Subsample cb and cr
            cb = SubSample(cb);
            cr = SubSample(cr);
            //Pad
            byte rowAdj = 0;
            byte colAdj = 0;
            y = Prepad(y, out int yRow, out int yCol, rowAdj, colAdj);
            cb = Prepad(cb, out int cbRow, out int cbCol, rowAdj, colAdj);
            cr = Prepad(cr, out int crRow, out int crCol, rowAdj, colAdj);
            //Create 8x8s
            var ySubset = Helper.CreateSubsets(y);
            var cbSubset = Helper.CreateSubsets(cb);
            var crSubset = Helper.CreateSubsets(cr);
            //DCT 8x8s
            ySubset = DCTNonThreadedRunnerSquared(ySubset);
            cbSubset = DCTNonThreadedRunnerSquared(cbSubset);
            crSubset = DCTNonThreadedRunnerSquared(crSubset);
            //Quantize
            ySubset = QuantizeY(ySubset);
            cbSubset = QuantizeY(cbSubset);
            crSubset = QuantizeY(crSubset);
            //Mogarithm
            var yBytes = MogarithmRunner(ySubset);
            var cbBytes = MogarithmRunner(cbSubset);
            var crBytes = MogarithmRunner(crSubset);
            //store length for unpacking
            Helper.SaveWidthHeight(ySubset, out int yWidth, out int yHeight);
            Helper.SaveWidthHeight(cbSubset, out int cbWidth, out int cbHeight);
            Helper.SaveWidthHeight(crSubset, out int crWidth, out int crHeight);
            //Combine bytes
            List<byte> combined = new();
            combined.AddRange(yBytes);
            combined.AddRange(cbBytes);
            combined.AddRange(crBytes);
            //compress
            var compressed = Helper.MRLE(combined.ToArray());
            Debug.WriteLine($"Compressed Size: {compressed.Length}");
            SaveJPEG(compressed, src.PixelWidth, src.PixelHeight, (byte)src.Format.BitsPerPixel,
                yBytes.Length, cbBytes.Length, (byte)yRow, (byte)yCol, (byte)cbRow, (byte)cbCol,
                rowAdj, colAdj, yWidth, yHeight, cbWidth, cbHeight);
        }

        public static void Mpegger(Image img1, Canvas c1, Image img2, Canvas c2)
        {
            Debug.Assert(img1 != null && img2 != null);
            var src1 = img1.Source as BitmapSource ?? throw new ArgumentNullException("Image does not exist");
            var image1 = ConvertData(src1);
            //Bytes to RGB
            var rgbpixels1 = RGB.MatrixBufferToRGB(image1);
            //RGB to YCbCr
            var ycbcrpixels1 = YCBCR.RGBMatrixToYCBCRMatrix(rgbpixels1);
            //Seperate channels
            YCBCR.DeconstructYCBCR(ycbcrpixels1, out float[,] y1, out float[,] cb1, out float[,] cr1);

            var src2 = img2.Source as BitmapSource ?? throw new ArgumentNullException("Literally impossible to be null");
            var image2 = ConvertData(src2);
            var rgbpixels2 = RGB.MatrixBufferToRGB(image2);
            var ycbcrpixels2 = YCBCR.RGBMatrixToYCBCRMatrix(rgbpixels2);
            YCBCR.DeconstructYCBCR(ycbcrpixels2, out float[,] y2, out float[,] cb2, out float[,] cr2);

            List<Point> points = new List<Point>();
            for (int i = 0; i < y1.GetLength(0); i += Constants.BLOCKS)
            {
                for (int j = 0; j < y1.GetLength(1); j += Constants.BLOCKS)
                {
                    points.Add(new Point(i, j));
                }
            }
            for (int i = 0; i < points.Count; ++i)
            {
                Line l = new();
                l.X1 = points[i].x;
                l.X2 = points[i].x + 1;
                l.Y1 = points[i].y;
                l.Y2 = points[i].y + 1;
                l.Stroke = Brushes.Red;
                l.StrokeThickness = 1;
                c1.Children.Add(l);
            }
            var tester = y1;
            var list = c1.Children.OfType<Line>().ToList();
            var yPoints = GetMotionVectors(y1, y2, list);
            var cbPoints = GetMotionVectors(cb1, cb2, list);
            var crPoints = GetMotionVectors(cr1, cr2, list);

            var yDiffs = GetDifferenceBlox(y2, y1, yPoints);
            var cbDiffs = GetDifferenceBlox(cb2, cb1, cbPoints);
            var crDiffs = GetDifferenceBlox(cr2, cr1, crPoints);

            y1 = ReassembleFromDifference(yDiffs, y1, yPoints);
            cb1 = ReassembleFromDifference(cbDiffs, cb1, cbPoints);
            cr1 = ReassembleFromDifference(crDiffs, cr1, crPoints);

            var reconstructedpixels = YCBCR.ReconstructYCBCR(y1, cb1, cr1);
            var rgbreconstruct = RGB.YCBCRtoRGB(reconstructedpixels);

            var buffer = Helper.MatrixToArray(RGB.RGBtoBuffer(rgbreconstruct));

            WriteableBitmap bmp = new(src1.PixelWidth, src1.PixelHeight, 96, 96,
                PixelFormats.Bgra32,
                null);
            bmp.WritePixels(
                new(0, 0, src1.PixelWidth, src1.PixelHeight),
                buffer,
                src1.PixelWidth * src1.Format.BitsPerPixel / 8,
                0);
            img2.Source = bmp;

        }

        public static float[,] ReassembleFromDifference(List<List<float[,]>> diffblocks, float[,] references, List<Point> vectors)
        {
            List<List<float[,]>> result = new();
            var reference = Helper.CreateSubsets(references);
            var mvectors = Helper.ArrayToMatrix(vectors.ToArray(), references.GetLength(0) / Constants.BLOCKS);
            for (int i = 0; i < reference.Count; ++i)
            {
                result.Add(new List<float[,]>());
                for (int j = 0; j < reference[i].Count; ++j)
                {
                    result[i].Add(Helper.AddMatrix(diffblocks[i][j]
                        , reference[i + mvectors[i, j].x / Constants.BLOCKS][j + mvectors[i, j].y / Constants.BLOCKS]));
                }
            }

            return Helper.ReassembleSubsets(result);
        }

        public static List<Point> GetMotionVectors(float[,] target, float[,] reference, List<Line> l)
        {
            List<Point> p = new();
            for (int x = 0; x < target.GetLength(0); x += Constants.BLOCKS)
            {
                for (int y = 0; y < target.GetLength(1); y += Constants.BLOCKS)
                {
                    p.Add(MoVector.SeqSearch(reference, target, y, x));
                }
            }
            for (int i = 0; i < l.Count; ++i)
            {
                //Debug.WriteIf(p[i].x < 0 || p[i].y < 0, $"Uh oh negative: ({p[i].x}, {p[i].y})\n");
                l[i].X1 = l[i].X1 + p[i].y;
                l[i].Y1 = l[i].Y1 + p[i].x;
            }
            return p;
        }

        public static List<List<float[,]>> GetDifferenceBlox(float[,] target, float[,] reference, List<Point> vectors)
        {
            var targetsubsets = Helper.CreateSubsets(target);
            var refersubsets = Helper.CreateSubsets(reference);
            List<List<float[,]>> diffblocks = new();
            var mvectors = Helper.ArrayToMatrix(vectors.ToArray(), target.GetLength(0) / Constants.BLOCKS);
            for (int i = 0; i < targetsubsets.Count; ++i)
            {
                diffblocks.Add(new List<float[,]>());
                for (int j = 0; j < targetsubsets[i].Count; ++j)
                {
                    diffblocks[i].Add(
                        Helper.SubtractMatrix(
                            targetsubsets[i][j],
                            refersubsets[i + mvectors[i, j].x / Constants.BLOCKS][j + mvectors[i, j].y / Constants.BLOCKS]
                            )
                        );
                }
            }
            return diffblocks;
        }

        public static void SaveJPEG(byte[] compressed,
            int pixWidth, int pixHeight, byte bpx, int yBytesLen, int cBytesLen,
            byte yPadW, byte yPadH, byte cPadW, byte cPadH,
            byte rowAdj, byte colAdj,
            int yWidth, int yHeight, int cWidth, int cHeight)
        {
            SaveFileDialog sfd = new();
            sfd.Filter = "Files Compressed by Mo (*.pegnt) | *.pegnt";
            sfd.DefaultExt = "pegnt";
            if (sfd.ShowDialog() == true)
            {
                FileStream fs = (FileStream)sfd.OpenFile();
                BinaryWriter writer = new(fs);
                writer.Write(pixWidth);
                writer.Write(pixHeight);
                writer.Write(bpx);
                writer.Write(yBytesLen);
                writer.Write(cBytesLen);
                writer.Write(yPadW);
                writer.Write(yPadH);
                writer.Write(cPadW);
                writer.Write(cPadH);
                writer.Write(yWidth);
                writer.Write(yHeight);
                writer.Write(cWidth);
                writer.Write(cHeight);
                writer.Write(compressed.Length);
                writer.Write(compressed);
                Debug.WriteLine($"Width: {pixWidth}");
                Debug.WriteLine($"Height: {pixHeight}");
                Debug.WriteLine($"Bits Per Pixel: {bpx}");
                Debug.WriteLine($"Y Length: {yBytesLen}");
                Debug.WriteLine($"C Length: {cBytesLen}");
                Debug.WriteLine($"Y Pad Needed (W): {yPadW}");
                Debug.WriteLine($"Y Pad Needed (H): {yPadH}");
                Debug.WriteLine($"C Pad Needed (W): {cPadW}");
                Debug.WriteLine($"C Pad Needed (H): {cPadH}");
                Debug.WriteLine($"Y Subsets (W): {yWidth}");
                Debug.WriteLine($"Y Subsets (H): {yHeight}");
                Debug.WriteLine($"C Subsets (W): {cWidth}");
                Debug.WriteLine($"C Subsets (H): {cHeight}");
                Debug.WriteLine($"Compressed Length: {compressed.Length}");
            }
            else
            {
                Debug.WriteLine($"Saving File Failed...");
            }
        }

        public static void OpenJPEG(out byte[] compressed,
            out int pixWidth, out int pixHeight, out int bpx, out int yBytesLen, out int cBytesLen,
            out int yPadW, out int yPadH, out int cPadW, out int cPadH,
            out int yWidth, out int yHeight, out int cWidth, out int cHeight)
        {
            OpenFileDialog ofd = new();
            ofd.Filter = "Files Compressed by Mo (*.pegnt) | *.pegnt";
            ofd.DefaultExt = "pegnt";
            if (ofd.ShowDialog() == true)
            {
                FileStream fs = (FileStream)ofd.OpenFile();
                BinaryReader reader = new(fs);
                pixWidth = reader.ReadInt32();
                pixHeight = reader.ReadInt32();
                bpx = reader.ReadByte();
                yBytesLen = reader.ReadInt32();
                cBytesLen = reader.ReadInt32();
                yPadW = reader.ReadByte();
                yPadH = reader.ReadByte();
                cPadW = reader.ReadByte();
                cPadH = reader.ReadByte();
                yWidth = reader.ReadInt32();
                yHeight = reader.ReadInt32();
                cWidth = reader.ReadInt32();
                cHeight = reader.ReadInt32();
                var length = reader.ReadInt32();
                compressed = reader.ReadBytes(length);
                Debug.WriteLine($"Width: {pixWidth}");
                Debug.WriteLine($"Height: {pixHeight}");
                Debug.WriteLine($"Bits Per Pixel: {bpx}");
                Debug.WriteLine($"Y Length: {yBytesLen}");
                Debug.WriteLine($"C Length: {cBytesLen}");
                Debug.WriteLine($"Y Pad Needed (W): {yPadW}");
                Debug.WriteLine($"Y Pad Needed (H): {yPadH}");
                Debug.WriteLine($"C Pad Needed (W): {cPadW}");
                Debug.WriteLine($"C Pad Needed (H): {cPadH}");
                Debug.WriteLine($"Y Subsets (W): {yWidth}");
                Debug.WriteLine($"Y Subsets (H): {yHeight}");
                Debug.WriteLine($"C Subsets (W): {cWidth}");
                Debug.WriteLine($"C Subsets (H): {cHeight}");
                Debug.WriteLine($"Compressed Length: {length}");
            }
            else
            {
                pixWidth = 0;
                pixHeight = 0;
                bpx = 0;
                yBytesLen = 0;
                cBytesLen = 0;
                yPadW = 0;
                yPadH = 0;
                cPadW = 0;
                cPadH = 0;
                yWidth = 0;
                yHeight = 0;
                cWidth = 0;
                cHeight = 0;
                compressed = null;
                Debug.WriteLine($"Opening File Failed...");
            }
        }

        public static BitmapSource OpenCompressed()
        {
            OpenJPEG(out byte[] compressed,
                out int pixWidth, out int pixHeight, out int bpx,
                out int yBytesLen, out int cBytesLen,
                out int yPadW, out int yPadH, out int cPadW, out int cPadH,
                out int yWidth, out int yHeight, out int cWidth, out int cHeight);
            //decompress
            var combined = Helper.Decompress(compressed.ToArray()).ToList();
            //unpack
            var yBytes = combined.GetRange(0, yBytesLen).ToArray();
            var cbBytes = combined.GetRange(yBytesLen, cBytesLen).ToArray();
            var crBytes = combined.GetRange(yBytesLen + cBytesLen, cBytesLen).ToArray();
            //Demogarithmize
            var ySubset = Demogarithmizer(yBytes, yWidth, yHeight);
            var cbSubset = Demogarithmizer(cbBytes, cWidth, cHeight);
            var crSubset = Demogarithmizer(crBytes, cWidth, cHeight);
            //Unquantize
            ySubset = DeQuantizeY(ySubset);
            cbSubset = DeQuantizeY(cbSubset);
            crSubset = DeQuantizeY(crSubset);
            //IDCT
            ySubset = IDCTNonThreadedRunnerSquared(ySubset);
            cbSubset = IDCTNonThreadedRunnerSquared(cbSubset);
            crSubset = IDCTNonThreadedRunnerSquared(crSubset);
            //Reassemble
            var y = Helper.ReassembleSubsets(ySubset);
            var cb = Helper.ReassembleSubsets(cbSubset);
            var cr = Helper.ReassembleSubsets(crSubset);
            //Unpad
            y = Unpad(y, yPadW, yPadH);
            cb = Unpad(cb, cPadW, cPadH);
            cr = Unpad(cr, cPadW, cPadH);
            //Unsample
            cb = Unsample(cb);
            cr = Unsample(cr);
            //Combine channels
            var ycbcrpixels = YCBCR.ReconstructYCBCR(y, cb, cr);
            //Writing to buffer
            var rgbpixels = RGB.YCBCRtoRGB(ycbcrpixels);
            var buffer2d = RGB.RGBtoBuffer(rgbpixels);
            var buffer = Helper.MatrixToArray(buffer2d);
            WriteableBitmap bmp = new(pixWidth, pixHeight, 96, 96,
                PixelFormats.Bgra32,
                null);
            bmp.WritePixels(
                new System.Windows.Int32Rect(0, 0, pixWidth, pixHeight),
                buffer,
                (pixWidth * bpx + 7) / 8,
                0);
            return bmp;
        }

        public static byte[] MogarithmRunner(List<List<float[,]>> subsets)
        {
            List<byte> ret = new();
            for (int i = 0; i < subsets.Count; ++i)
            {
                for (int j = 0; j < subsets[i].Count; ++j)
                {
                    ret.AddRange(Helper.Mogarithm(subsets[i][j]));
                }
            }
            return ret.ToArray();
        }

        public static List<List<float[,]>> Demogarithmizer(byte[] channel, int width, int height)
        {
            List<float[,]> temp = new();
            var channellist = channel.ToList();
            for (int i = 0; i < channel.Length; i += 64)
            {
                temp.Add(Helper.InversentMogarithm(channellist.GetRange(i, 64).ToArray()));
            }
            foreach (var subset in temp)
            {
                for (int i = 0; i < subset.GetLength(0); ++i)
                {
                    for (int j = 0; j < subset.GetLength(1); ++j)
                    {
                        subset[i, j] -= 128;
                    }
                }
            }
            List<List<float[,]>> ret = new();
            for (int i = 0; i < height; ++i)
            {
                ret.Add(new List<float[,]>());
                for (int j = 0; j < width; ++j)
                {
                    ret[i].Add(temp[i * width + j]);
                }
            }
            return ret;
        }

        public static float[,] Unsample(float[,] arr)
        {
            float[,] ret = new float[arr.GetLength(0) * 2, arr.GetLength(1) * 2];
            for (int i = 0; i < ret.GetLength(0) - 1; i += 2)
            {
                for (int j = 0; j < ret.GetLength(1) - 1; j += 2)
                {
                    ret[i, j] = arr[i / 2, j / 2];
                    ret[i + 1, j + 1] = arr[i / 2, j / 2];
                    ret[i, j + 1] = arr[i / 2, j / 2];
                    ret[i + 1, j] = arr[i / 2, j / 2];
                }
            }
            return ret;
        }

        public static float[,] Unpad(float[,] arr, int rowDivis, int colDivis)
        {
            if (rowDivis == 0 && colDivis == 0) return arr;
            int row = (arr.GetLength(0) - rowDivis) % 2 == 0 ? arr.GetLength(0) - rowDivis : arr.GetLength(0) - rowDivis + 1;
            int col = (arr.GetLength(1) - colDivis) % 2 == 0 ? arr.GetLength(1) - colDivis : arr.GetLength(1) - colDivis + 1;
            float[,] ret = new float[row, col];
            for (int i = 0; i < ret.GetLength(0); ++i)
            {
                for (int j = 0; j < ret.GetLength(1); ++j)
                {
                    ret[i, j] = arr[i, j];
                }
            }
            return ret;
        }

        public static float[,] Prepad(float[,] arr, out int rowDivis, out int colDivis, byte rowAdj, byte colAdj)
        {
            rowDivis = 0;
            colDivis = 0;
            if (arr.GetLength(0) % 2 == 0) rowAdj = 0;
            else rowAdj = 1;
            if (arr.GetLength(1) % 2 == 0) colAdj = 0;
            else colAdj = 1;
            while ((arr.GetLength(0) + rowDivis) % Constants.MATRIX_SIZE != 0)
            {
                ++rowDivis;
            }
            while ((arr.GetLength(1) + colDivis) % Constants.MATRIX_SIZE != 0)
            {
                ++colDivis;
            }
            float[,] ret = new float[arr.GetLength(0) + rowDivis, arr.GetLength(1) + colDivis];
            for (int i = 0; i < arr.GetLength(0); ++i)
            {
                for (int j = 0; j < arr.GetLength(1); ++j)
                {
                    ret[i, j] = arr[i, j];
                }
            }
            return ret;
        }

        private static List<List<float[,]>> QuantizeY(List<List<float[,]>> y)
        {
            List<List<float[,]>> ret = new();
            for (int i = 0; i < y.Count; ++i)
            {
                ret.Add(new List<float[,]>());
                for (int j = 0; j < y[i].Count; ++j)
                {
                    ret[i].Add(new float[0, 0]);
                    ret[i][j] = Helper.QuantizeLuminosity(y[i][j]);
                }
            }
            return ret;
        }

        private static List<List<float[,]>> QuantizeC(List<List<float[,]>> c)
        {
            List<List<float[,]>> ret = new();
            for (int i = 0; i < c.Count; ++i)
            {
                ret.Add(new List<float[,]>());
                for (int j = 0; j < c[i].Count; ++j)
                {
                    ret[i].Add(new float[0, 0]);
                    ret[i][j] = Helper.QuantizeChrominance(c[i][j]);
                }
            }
            return ret;
        }

        private static List<List<float[,]>> DeQuantizeY(List<List<float[,]>> y)
        {
            List<List<float[,]>> ret = new();
            for (int i = 0; i < y.Count; ++i)
            {
                ret.Add(new List<float[,]>());
                for (int j = 0; j < y[i].Count; ++j)
                {
                    ret[i].Add(new float[0, 0]);
                    ret[i][j] = Helper.DeQuantizeLuminosity(y[i][j]);
                }
            }
            return ret;
        }

        private static List<List<float[,]>> DeQuantizeC(List<List<float[,]>> c)
        {
            List<List<float[,]>> ret = new();
            for (int i = 0; i < c.Count; ++i)
            {
                ret.Add(new List<float[,]>());
                for (int j = 0; j < c[i].Count; ++j)
                {
                    ret[i].Add(new float[0, 0]);
                    ret[i][j] = Helper.DeQuantizeChrominance(c[i][j]);
                }
            }
            return ret;
        }

        private List<List<float[,]>> DCTRunnerSquared(List<List<float[,]>> img)
        {
            List<List<float[,]>> returnable = new();
            foreach (var subset in img)
            {
                returnable.Add(DCTRunner(subset));
            }
            return returnable;
        }

        private List<float[,]> DCTRunner(List<float[,]> img)
        {
            int tasks = img.Count() / Constants.MAX_THREADS;
            List<float[,]> result = new();
            List<Task<List<float[,]>>> threads = new();
            for (int t = 0; t < Constants.MAX_THREADS; ++t)
            {
                threads.Add(Task.Factory.StartNew(() =>
                {
                    List<float[,]> returnable = new();
                    for (int i = 0; i < tasks; ++i)
                    {
                        returnable.Add(DCT(img[t * tasks + i]));
                        if (i == tasks - 1) break;
                    }
                    return returnable;
                }));
                if (t == Constants.MAX_THREADS - 1) break;
            }
            Task.WaitAll(threads.ToArray());
            for (int t = 0; t < Constants.MAX_THREADS; ++t)
            {
                for (int i = 0; i < threads[t].Result.Count(); ++i)
                {
                    result.Add(threads[t].Result[i]);
                    if (i == threads[t].Result.Count() - 1) break;
                }
                if (t == Constants.MAX_THREADS - 1) break;
            }
            return result;
        }

        private List<List<float[,]>> DCTNonThreadedRunnerSquared(List<List<float[,]>> img)
        {
            List<List<float[,]>> ret = new();
            foreach (var list in img)
            {
                ret.Add(DCTNonThreadedRunner(list));
            }
            return ret;
        }

        private List<float[,]> DCTNonThreadedRunner(List<float[,]> img)
        {
            List<float[,]> ret = new();
            foreach (var subset in img)
            {
                ret.Add(DCT(subset));
            }
            return ret;
        }

        private static List<List<float[,]>> IDCTNonThreadedRunnerSquared(List<List<float[,]>> img)
        {
            List<List<float[,]>> ret = new();
            foreach (var list in img)
            {
                ret.Add(IDCTNonThreadedRunner(list));
            }
            return ret;
        }

        private static List<float[,]> IDCTNonThreadedRunner(List<float[,]> img)
        {
            List<float[,]> ret = new();
            foreach (var subset in img)
            {
                ret.Add(InverseDCT(subset));
            }
            return ret;
        }

        private static List<List<float[,]>> IDCTRunnerSquared(List<List<float[,]>> img)
        {
            List<List<float[,]>> returnable = new();
            foreach (var subset in img)
            {
                returnable.Add(IDCTRunner(subset));
            }
            return returnable;
        }

        private static List<float[,]> IDCTRunner(List<float[,]> img)
        {
            int tasks = img.Count() / Constants.MAX_THREADS;
            List<float[,]> result = new();
            List<Task<List<float[,]>>> threads = new();
            for (int t = 0; t < Constants.MAX_THREADS; ++t)
            {
                threads.Add(Task.Factory.StartNew(() =>
                {
                    List<float[,]> returnable = new();
                    for (int i = 0; i < tasks; ++i)
                    {
                        returnable.Add(InverseDCT(img[t * tasks + i]));
                        if (i == tasks - 1) break;
                    }
                    return returnable;
                }));
                if (t == Constants.MAX_THREADS - 1) break;
            }
            Task.WaitAll(threads.ToArray());
            for (int t = 0; t < Constants.MAX_THREADS; ++t)
            {
                for (int i = 0; i < threads[t].Result.Count(); ++i)
                {
                    result.Add(threads[t].Result[i]);
                    if (i == threads[t].Result.Count() - 1) break;
                }
                if (t == Constants.MAX_THREADS - 1) break;
            }
            return result;
        }

        private static float[,] DCT(float[,] h)
        {
            int width = h.GetLength(0);
            int height = h.GetLength(1);
            float[,] H = new float[width, height];
            float accumulator;
            for (int u = 0; u < width; ++u)
            {
                for (int v = 0; v < height; ++v)
                {
                    accumulator = 0;
                    for (int x = 0; x < width; ++x)
                    {
                        for (int y = 0; y < height; ++y)
                        {
                            accumulator += MathF.Cos((u * MathF.PI * (2 * x + 1)) / (2 * width))
                                * MathF.Cos((v * MathF.PI * (2 * y + 1)) / (2 * height))
                                * h[x, y];
                        }
                    }
                    H[u, v] = FloatRound(accumulator * (2 / MathF.Sqrt(height * width)) * C(u) * C(v));
                }
            }
            return H;
        }

        private static float[,] InverseDCT(float[,] H)
        {
            int width = H.GetLength(0);
            int height = H.GetLength(1);
            float[,] h = new float[width, height];
            float accumulator;
            for (int x = 0; x < width; ++x)
            {
                for (int y = 0; y < height; ++y)
                {
                    accumulator = 0;
                    for (int u = 0; u < width; ++u)
                    {
                        for (int v = 0; v < height; ++v)
                        {
                            accumulator += C(u) * C(v)
                                * MathF.Cos(u * MathF.PI * (2 * x + 1) / (2 * width))
                                * MathF.Cos(v * MathF.PI * (2 * y + 1) / (2 * height))
                                * H[u, v];
                        }
                    }
                    h[x, y] = FloatRound(accumulator * (2 / MathF.Sqrt(height * width)));
                }
            }
            return h;
        }

        private static float C(int num)
        {
            return num == 0 ? (float)(1 / Math.Sqrt(2)) : 1;
        }

        public static float[,] SubSample(float[,] pComponent)
        {
            float[,] component = new float[pComponent.GetLength(0) / 2, pComponent.GetLength(1) / 2];
            for (int i = 0; i < pComponent.GetLength(0) - 1; i += 2)
            {
                for (int j = 0; j < pComponent.GetLength(1) - 1; j += 2)
                {
                    component[i / 2, j / 2] = pComponent[i, j];
                }
            }
            return component;
        }

        public static float[,] ConvertData(BitmapSource src)
        {
            int stride = src.PixelWidth * src.Format.BitsPerPixel / 8;
            int total = src.PixelWidth * src.PixelHeight * src.Format.BitsPerPixel / 8;
            int stridenums = total / stride;
            byte[] buffer = new byte[total];
            src.CopyPixels(buffer, stride, 0);
            Debug.WriteLine($"Original Size: {buffer.Length}");
            float[,] data = new float[stridenums, stride];

            for (int y = 0; y < stride; ++y)
            {
                for (int x = 0; x < stridenums; ++x)
                {
                    data[x, y] = buffer[x * stride + y];
                }
            }
            return data;
        }

        private static float FloatRound(float f)
        {
            float r = f < 0 ? (int)f - 0.5f : (int)f + 0.5f;
            return r == -0 ? 0 : r;
        }
    }
}
