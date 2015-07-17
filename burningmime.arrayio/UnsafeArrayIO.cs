﻿using System;
using System.Collections.Generic;
using System.IO;

namespace burningmime.arrayio
{
    /// <summary>
    /// Allows fast, unsafe blitting of structure arrays to and from streams, similar to what you can do in C/C++. Because of
    /// https://en.wikipedia.org/wiki/Endianness , the written data is not guaranteed to be compatible between processor architectures --
    /// in particular, it's not compatible with regular <see cref="BinaryReader"/> and <see cref="BinaryWriter"/>, nor with many
    /// network protocols. It can provide a gargantuan performance advantage against BinaryReader/BinaryWriter in certain cases, just
    /// be sure it's both written and read using this class.
    /// 
    /// USE AT YOUR OWN RISK!
    /// </summary>
    public static class UnsafeArrayIO
    {
        /// <summary>
        /// Reads an array of type T[] directly from the stream without doing any processing. Assumes the endianess of the values
        /// are the same as the processor (which means you cannot have used a regular <see cref="BinaryWriter"/> to write it, since
        /// that writes big-endian on x86/x64, which are little-endian processors). Does not do any type checking; simply blits the
        /// memory.
        /// </summary>
        /// <typeparam name="T">The array type (used in a fixed() expression -- so it must not contain any managed references).</typeparam>
        /// <param name="stream">Stream to read from.</param>
        /// <param name="elementCount">Number of elements to read (not the number of bytes -- to read 2 ints, pass 2, not 8).</param>
        /// <returns>The correctly typed array.</returns>
        public static T[] ReadArray<T>(Stream stream, int elementCount) where T : struct
        {
            if(elementCount <= 0)
            {
                return new T[0];
            }

            ArrayConverter converter = GetConverter<T>();
            int nBytes = elementCount * converter.SizeOf;
            byte[] buffer = new byte[nBytes];
            stream.Read(buffer, 0, nBytes);
            return (T[]) converter.ConvertFromByte(buffer, elementCount, true);
        }

        /// <summary>
        /// Reads an array of type T[] directly from the stream without doing any processing. Assumes the endianess of the values
        /// are the same as the processor (which means you cannot have used a regular <see cref="BinaryWriter"/> to write it, since
        /// that writes big-endian on x86/x64, which are little-endian processors). Does not do any type checking; simply blits the
        /// memory.
        /// </summary>
        /// <typeparam name="T">The array type (used in a fixed() expression -- so it must not contain any managed references).</typeparam>
        /// <param name="stream">Stream to read from.</param>
        /// <param name="elementCount">Number of elements to read (not the number of bytes -- to read 2 ints, pass 2, not 8).</param>
        /// <returns>The correctly typed array.</returns>
        public static T[] ReadArray<T>(BinaryReader stream, int elementCount) where T : struct
        {
            if(elementCount <= 0)
            {
                return new T[0];
            }

            ArrayConverter converter = GetConverter<T>();
            int nBytes = elementCount * converter.SizeOf;
            byte[] buffer = new byte[nBytes];
            stream.Read(buffer, 0, nBytes);
            return (T[]) converter.ConvertFromByte(buffer, elementCount, true);
        }

        /// <summary>
        /// Reads an array of type T[] directly from the stream without doing any processing. Writes values with the same endianess
        /// as the processor (which means you cannot use a regular <see cref="BinaryReader"/> to read it, since that reads big-endian
        /// on x86/x64, which are little-endian processors). Does not do any type checking; simply blits the memory.
        /// </summary>
        /// <typeparam name="T">The array type (used in a fixed() expression -- so it must not contain any managed references).</typeparam>
        /// <param name="stream">Stream to write to.</param>
        /// <param name="array">Array to write.</param>
        /// <param name="isThreadSafe">If you are 200% absolutely sure no other threads will access the array, you can set this to true
        /// to prevent a temporary copy from being made. If you set this to true and another thread is accessing the array, the runtime
        /// will likely crash or unexpected behavior will happen. If even a tiny bit unsure, leave this as false.</param>
        public static void WriteArray<T>(Stream stream, T[] array, bool isThreadSafe = false) where T : struct
        {
            if(array == null || array.Length == 0)
            {
                return;
            }

            if(!isThreadSafe)
            {
                // need to create a duplicate of the array
                T[] copy = new T[array.Length];
                Array.Copy(array, copy, array.Length);
                array = copy;
            }

            ArrayConverter converter = GetConverter<T>();
            int elementCount = array.Length;
            int nBytes = elementCount * converter.SizeOf;
            byte[] buffer = converter.ConvertToByte(array, nBytes);
            try
            {
                stream.Write(buffer, 0, buffer.Length);
            }
            finally
            {
                if(isThreadSafe)
                {
                    // If we changed the original array type, change it back.
                    converter.ConvertFromByte(buffer, elementCount, false);
                }
            }
        }

        /// <summary>
        /// Reads an array of type T[] directly from the stream without doing any processing. Writes values with the same endianess
        /// as the processor (which means you cannot use a regular <see cref="BinaryReader"/> to read it, since that reads big-endian
        /// on x86/x64, which are little-endian processors). Does not do any type checking; simply blits the memory.
        /// </summary>
        /// <typeparam name="T">The array type (used in a fixed() expression -- so it must not contain any managed references).</typeparam>
        /// <param name="stream">Stream to write to.</param>
        /// <param name="array">Array to write.</param>
        /// <param name="isThreadSafe">If you are 200% absolutely sure no other threads will access the array, you can set this to true
        /// to prevent a temporary copy from being made. If you set this to true and another thread is accessing the array, the runtime
        /// will likely crash or unexpected behavior will happen. If even a tiny bit unsure, leave this as false.</param>
        public static void WriteArray<T>(BinaryWriter stream, T[] array, bool isThreadSafe = false) where T : struct
        {
            if(array == null || array.Length == 0)
            {
                return;
            }

            if(!isThreadSafe)
            {
                // need to create a duplicate of the array
                T[] copy = new T[array.Length];
                Array.Copy(array, copy, array.Length);
                array = copy;
            }

            ArrayConverter converter = GetConverter<T>();
            int elementCount = array.Length;
            int nBytes = elementCount * converter.SizeOf;
            byte[] buffer = converter.ConvertToByte(array, nBytes);
            try
            {
                stream.Write(buffer, 0, buffer.Length);
            }
            finally
            {
                if(isThreadSafe)
                {
                    // If we changed the original array type, change it back.
                    converter.ConvertFromByte(buffer, elementCount, false);
                }
            }
        }
        
        /// <summary>
        /// Cache of converters we've generated.
        /// </summary>
        private static readonly Dictionary<Type, ArrayConverter> _converters = new Dictionary<Type, ArrayConverter>();

        /// <summary>
        /// Gets or creates a converter for the given type.
        /// </summary>
        private static ArrayConverter GetConverter<T>()
        {
            ArrayConverter result;
            Type type = typeof(T);
            if(!_converters.TryGetValue(type, out result))
            {
                result = new ArrayConverter(type, new T[1]);
                _converters[type] = result;
            }
            return result;
        }
    }
}