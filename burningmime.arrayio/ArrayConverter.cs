using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;

namespace burningmime.arrayio
{
    /// <summary>
    /// Class which allows for unsafe conversion of arrays from one type to another. Does not create copies of the arrays, instead
    /// simply changes the runtime type of the array to fool the CLR into thinking it's a different type.
    /// </summary>
    internal sealed unsafe class ArrayConverter
    {
        private static readonly IntPtr _pMT_byte;              // pointer to method table for byte[]
        private static readonly int _methodTableOffset;        // offset (in units of size_t bytes) of method table from start of array data. -2 for .NET CLR, -4 for Mono
        private static readonly MethodInfo _changeToByte2;     // MethodInfo for the ChangeToByte2 method
        private static readonly Module _myModule;              // module in which this type exists
        private static readonly bool _mustAlignDoubles;        // do we need to check double alignment on this platform?

        private readonly IntPtr _pMT;                          // pointer to method table for T[]
        private readonly Action<object, int> _changeToByte1;   // generated shim which pins the array pointer
        private readonly int _sizeOf;                          // sizeof(T)
        private readonly bool _alignTo8Bytes;                  // true iff this is a converter for double[] and the platform should align double arrays

        static ArrayConverter()
        {
            bool isMono = Type.GetType("Mono.Runtime") != null;
            _methodTableOffset = isMono ? -4 : -2;
            _changeToByte2 = typeof(ArrayConverter).GetMethod("ChangeToByte2", BindingFlags.Static | BindingFlags.Public);
            _myModule = typeof(ArrayConverter).Module;
            _mustAlignDoubles = sizeof(IntPtr) == 4;
            byte[] bytes = new byte[1];
            fixed(byte* p = bytes)
                _pMT_byte = ((IntPtr*) p)[_methodTableOffset];
        }

        /// <summary>
        /// Creates a new ArrayConverter for the given type.
        /// </summary>
        /// <param name="baseType">Type to create.</param>
        /// <param name="oneElemArray">Array of the given type with length 1 or more (used to get method table pointer).</param>
        public ArrayConverter(Type baseType, object oneElemArray)
        {
            _alignTo8Bytes = _mustAlignDoubles && baseType == typeof(double);
            _sizeOf = Marshal.SizeOf(baseType); // do this first since it throws a nice error for non-unmanaged types
            Type arrayType = baseType.MakeArrayType();
            Type refType = baseType.MakeByRefType();
            _pMT = GetMethodTablePointer(baseType, arrayType, refType, oneElemArray);
            _changeToByte1 = CreateChangeToByte1(baseType, arrayType, refType);
        }

        /// <summary>
        /// Gets the size in bytes of a single array element of this type.
        /// </summary>
        public int SizeOf
        {
            get { return _sizeOf; }
        }

        /// <summary>
        /// Changes the array type to T[] and the length to newLen. Modifies the array itself; does not return a copy of the array.
        /// This is very, very thread-unsafe. Be 200% sure that no other thread can access the array when it's in its changed state.
        /// 
        /// The array must not be null or empty.
        /// </summary>
        public object ConvertFromByte(byte[] buffer, int newSize, bool allowRealloc)
        {
            bool realign;
            fixed(byte* p = buffer)
            {
                ChangeArrayType((IntPtr*) p, newSize, _pMT);
                realign = allowRealloc && _alignTo8Bytes && (long) p % 8 != 0;
            }

            if(realign)
            {
                double[] src = (double[]) (object) buffer;
                double[] dst = new double[src.Length];
                Array.Copy(src, dst, src.Length);
                return dst;
            }
            else
            {
                return buffer;
            }
        }

        /// <summary>
        /// Changes the array type to byte[] and the length to newLen. Modifies the array itself; does not return a copy of the array.
        /// This is very, very thread-unsafe. Be 200% sure that no other thread can access the array when it's in its changed state.
        /// 
        /// The array must not be null or empty.
        /// </summary>
        public byte[] ConvertToByte(object array, int newSize)
        {
            _changeToByte1(array, newSize);
            return (byte[]) array;
        }

        /// <summary>
        /// Called by the generated shim for changing T[] to byte[]. Just calls <see cref="ChangeArrayType"/> with the correct args.
        /// Note this method is public so it can be accessed by the generated code. It should not be used from outside this class.
        /// </summary>
        public static void ChangeToByte2(IntPtr* p, int newSize)
        {
            ChangeArrayType(p, newSize, _pMT_byte);
        }

        /// <summary>
        /// Method which actually changes the size and type of an array. Uses a constrained region to make sure this is done
        /// in one atomic operation (so as not to *completely* break the runtime).
        /// </summary>
        [ReliabilityContract(Consistency.MayCorruptAppDomain, Cer.MayFail)]
        private static void ChangeArrayType(IntPtr* p, int newSize, IntPtr pMT)
        {
            int ofs = _methodTableOffset;

            // We need to use a CER here so that the method table pointer and length are changed in one atomic
            // operation. We do this to prevent out-of-band exceptions (specifically, ThreadAbortException) from
            // being raised when the size has been changed but not the type.
            RuntimeHelpers.PrepareConstrainedRegions();
            try { }
            finally
            {
                p[-1] = (IntPtr) newSize;
                p[ofs] = pMT;
            }
        }

        /// <summary>
        /// Gets the pointer to the method table for the array type.
        /// </summary>
        /// <remarks>
        /// The generated function is basically...
        /// 
        /// <code>
        ///     private static IntPtr GetMethodTablePointer_T(T[] array)
        ///     {
        ///         fixed(T* p = array)
        ///         {
        ///             return ((IntPtr*) p)[_methodTableOffset];
        ///         }
        ///     }
        /// </code>
        /// </remarks>
        private static IntPtr GetMethodTablePointer(Type baseType, Type arrayType, Type refType, object oneElemArray)
        {
            string mname = "GetMethodTablePointer_" + baseType.Name;
            DynamicMethod method = new DynamicMethod(mname, typeof(IntPtr), new[] { arrayType }, _myModule);
            ILGenerator il = method.GetILGenerator();
            il.DeclareLocal(refType, true);
            il.Emit(OpCodes.Ldarg_0);                     // Load the array
            il.Emit(OpCodes.Ldc_I4_0);                    // Push a 0 offset
            il.Emit(OpCodes.Ldelema, baseType);           // Get address of array data
            il.Emit(OpCodes.Stloc_0);                     // Pin array
            il.Emit(OpCodes.Ldloc_0);                     // Load pinned (the store/load here is required for pinning)
            il.Emit(OpCodes.Conv_I);                      // Convert to IntPtr
            il.Emit(OpCodes.Ldc_I4, _methodTableOffset);  // Load offset of method table pointer
            il.Emit(OpCodes.Conv_I);                      // Convert that to a ptrdiff_t
            il.Emit(OpCodes.Sizeof, typeof(IntPtr));      // Load sizeof(size_t)
            il.Emit(OpCodes.Mul);                         // Multiply offset * sizeof(size_t) to get offset in bytes
            il.Emit(OpCodes.Add);                         // Add that to the array data offset
            il.Emit(OpCodes.Ldind_I);                     // Get element at that address
            il.Emit(OpCodes.Ret);                         // Return it
            return (IntPtr) method.Invoke(null, new[] { oneElemArray });
        }

        /// <summary>
        /// Generates a shim function that pins an array pointer and calls <see cref="ChangeToByte2"/> with it.
        /// </summary>
        /// <remarks>
        /// The generated function is basically...
        /// 
        /// <code>
        ///     private static IntPtr ChangeToByte1_T(object array, int newSize)
        ///     {
        ///         fixed(T* p = (T[]) array)
        ///         {
        ///             ChangeToByte2((IntPtr*) p, newSize);
        ///         }
        ///     }
        /// </code>
        /// </remarks>
        private static Action<object, int> CreateChangeToByte1(Type baseType, Type arrayType, Type refType)
        {
            string mname = "ChangeToByte1_" + baseType.Name;
            DynamicMethod method = new DynamicMethod(mname, null, new[] { typeof(object), typeof(int) }, _myModule);
            ILGenerator il = method.GetILGenerator();
            il.DeclareLocal(refType, true);
            il.Emit(OpCodes.Ldarg_0);               // Load the array
            il.Emit(OpCodes.Castclass, arrayType);  // Cast to the correct type
            il.Emit(OpCodes.Ldc_I4_0);              // Push a 0 offset
            il.Emit(OpCodes.Ldelema, baseType);     // Get address of array data
            il.Emit(OpCodes.Stloc_0);               // Pin array
            il.Emit(OpCodes.Ldloc_0);               // Load pinned (the store/load here is required for pinning)
            il.Emit(OpCodes.Conv_I);                // Convert to IntPtr
            il.Emit(OpCodes.Ldarg_1);               // Load the newSize parameter
            il.Emit(OpCodes.Call, _changeToByte2);  // Call method that actually does stuff
            il.Emit(OpCodes.Ret);                   // Return
            return (Action<object, int>) method.CreateDelegate(typeof(Action<object, int>));
        }
    }
}