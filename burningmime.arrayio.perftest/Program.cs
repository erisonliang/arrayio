using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;

namespace burningmime.arrayio.perftest
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            Matrix4x4[] data = CreateTestData();
            Console.WriteLine("Test data: " + data.Length + " matrices = " + (data.LongLength * Marshal.SizeOf(typeof(Matrix4x4)) / 1024.0 / 1024.0).ToString("#.##") + "MiB");
            string filename = Path.GetTempFileName();
            try
            {
                Stopwatch sw = new Stopwatch();

                sw.Restart();
                TestBinaryWriterAndReader(filename, data);
                sw.Stop();
                Console.WriteLine("BinaryWriter/BinaryReader: " + sw.Elapsed.TotalMilliseconds + "ms");
                
                sw.Restart();
                TestUnsafeIO(filename, data);
                sw.Stop();
                Console.WriteLine("UnsafeArrayIO: " + sw.Elapsed.TotalMilliseconds + "ms");
                Console.WriteLine();
            }
            finally
            {
                File.Delete(filename);
            }
        }

        private static void TestBinaryWriterAndReader(string filename, Matrix4x4[] data)
        {
            using(Stream s = File.Create(filename))
            using(BinaryWriter w = new BinaryWriter(s))
            {
                foreach(Matrix4x4 m in data)
                {
                    w.Write(m.M11);
                    w.Write(m.M12);
                    w.Write(m.M13);
                    w.Write(m.M14);
                    w.Write(m.M21);
                    w.Write(m.M22);
                    w.Write(m.M23);
                    w.Write(m.M24);
                    w.Write(m.M31);
                    w.Write(m.M32);
                    w.Write(m.M33);
                    w.Write(m.M34);
                    w.Write(m.M41);
                    w.Write(m.M42);
                    w.Write(m.M43);
                    w.Write(m.M44);
                }
            }

            Matrix4x4[] result = new Matrix4x4[data.Length];
            using(Stream s = File.OpenRead(filename))
            using(BinaryReader r = new BinaryReader(s))
            {
                for(int i = 0; i < result.Length; i++)
                {
                    Matrix4x4 m;
                    m.M11 = r.ReadSingle();
                    m.M12 = r.ReadSingle();
                    m.M13 = r.ReadSingle();
                    m.M14 = r.ReadSingle();
                    m.M21 = r.ReadSingle();
                    m.M22 = r.ReadSingle();
                    m.M23 = r.ReadSingle();
                    m.M24 = r.ReadSingle();
                    m.M31 = r.ReadSingle();
                    m.M32 = r.ReadSingle();
                    m.M33 = r.ReadSingle();
                    m.M34 = r.ReadSingle();
                    m.M41 = r.ReadSingle();
                    m.M42 = r.ReadSingle();
                    m.M43 = r.ReadSingle();
                    m.M44 = r.ReadSingle();
                    result[i] = m;
                }
            }
            
            Verify(data, result);
        }

        private static void TestUnsafeIO(string filename, Matrix4x4[] data)
        {
            using (Stream s = File.Create(filename))
            {
                UnsafeArrayIO.WriteArray(s, data, true);
            }

            Matrix4x4[] result;
            using (Stream s = File.OpenRead(filename))
            {
                result = UnsafeArrayIO.ReadArray<Matrix4x4>(s, data.Length);
            }
            
            Verify(data, result);
        }

        private static Matrix4x4[] CreateTestData()
        {
            int N_ITEMS = 500000;
            Matrix4x4[] data = new Matrix4x4[N_ITEMS];
            Random r = new Random();
            for(int i = 0; i < data.Length; i++)
            {
                float rotX = (float) (r.NextDouble() * Math.PI * 2);
                float rotY = (float) (r.NextDouble() * Math.PI * 2);
                float rotZ = (float) (r.NextDouble() * Math.PI * 2);
                data[i] = Matrix4x4.CreateFromYawPitchRoll(rotX, rotY, rotZ);
            }
            return data;
        }

        private static void Verify(Matrix4x4[] original, Matrix4x4[] check)
        {
            if(original.Length != check.Length) throw new InvalidOperationException("Original and result data have different lengths!");
            for(int i = 0; i < original.Length; i++)
                if(original[i] != check[i])
                    throw new InvalidOperationException("Data mismatch: original[" + i + "] != check[" + i + "]");
        }
    }
}
