# Reading and Writing Binary Data 3x Faster In .NET Using Unsafe Code

### Performance Comparison

OK, graph first:

![perf-chart-0.png](/perf-chart-0.png)

This is the average performance of writing, then reading 500,000 matrices (about 30.5 MB of data) using either traditional BinaryWriter/BinaryReader
methods or by directly blitting the memory On my computer (which has an SSD, so the actual I/O is quite fast), it's about **3.232x faster**
using direct memory blitting than using the traditional methods. This is the average of 1000 runs.

[Test program](/burningmime.arrayio.perftest/Program.cs)  
[Result data (lots of numbers)](/results.txt) -- first column is using BinaryReader/BinaryWriter, second column is UnsafeArrayIO

### What is this?

C/C++ programmers are familiar with the idea of being able to read or write large amounts of data into a `void*`/`char*` then casting it to the correct type.
This is useful for things like large arrays of numbers for scientific applications or custom vertex data in graphics applications. Unfortunately, .NET does not
directly provide the ability to cast a `byte[]` to a `double[]`, or `Vector3[]`, or whatever. So, if you wanted to read 10,000 vectors from a file, you'd need to do something like:

```C#
Vector3[] vectors = new Vector3[10000];
using(Stream stream = File.OpenRead(filename))
{
    using(BinaryReader reader = new BinaryReader(stream))
    {
        for(int i = 0; i < vectors.Length; i++)
        {
            vectors[i].X = reader.ReadSingle();
            vectors[i].Y = reader.ReadSingle();
            vectors[i].Z = reader.ReadSingle();
        }
    }
}
```

This is quite slow, and, in the case of complex structures like matrices or vertex formats, could be error-prone. Wouldn't it be easier if there was a library that could just do this like:

```C#
Vector3[] vectors;
using(Stream stream = File.OpenRead(filename))
{
    vectors = UnsafeArrayIO.ReadArray<Vector3>(stream, 10000);
}
```

Well, now there is! By completely throwing type checking and endianness conversion (more on that later) out the window, you can do this much faster and easier. Internally, the library reads
the data in as a `byte[]` then uses some extremely unsafe and undocumented methods to trick the runtime into thinking the `byte[]` is actually a `Vector3[]`. It works fine for built-in types like
`float[]`, `int[]`, etc, too.

Writing an array can be done in a similar manner:

```C#
Vector3[] vectors = /* whatever */;
using(Stream stream = File.Create(filename))
{
    UnsafeArrayIO.WriteArray(stream, vectors);
}
```

If you're 200% sure the array will not be accessed by a different thread during the write operation, you can do:

```C#
UnsafeArrayIO.WriteArray(stream, vectors, true);
```

This way is a little faster and less memory-intensive since it doesn't create a copy of the array, but will lead to undefined behavior (probably a hard crash) if another thread tries to access the array while it's being written.

### But there are some caveats...

* This only works in a full-trust environment since it uses unsafe code and code generation. No Silverlight, Windows Phone, MonoTouch, etc.
* This only works for non-managed data types. No strings, no class references, nada. If the type in question can't be used in a `fixed()` expression, don't try to use it here. The type needs to have a fixed runtime size.
* It is dependent on the [Endianess](https://en.wikipedia.org/wiki/Endianness) of the processor. In practice, most processors today are little-endian (ARM is bi-endian but most of the time it's running in little-endian mode). This will
mainly impact its ability to be used on certain networking protocols since network byte order is big-endian. .NET's built-in BinaryWriter and BinaryReader read/write in big endian, too. So basically if you write it
using this library, you should read it using this library (or a C/C++ program that uses the same byte order).
* For structures such as vertex formats, versioning is an issue (but it's an issue with the BinaryReader method, too). Make sure the data is written in the same format it's read in.
* It doesn't work with arrays which have [non-default lower and upper bounds](https://msdn.microsoft.com/en-us/library/system.array.getlowerbound(v=vs.110).aspx). If you don't know what this is, don't worry about it.
* It doesn't work for `stackalloc`ed arrays.
* **It's very untested and completely not ready for production use.** Ultimately, the plan is to use this in a Unity game I'm writing, so I'll test it quite a bit more on Mono over time. At this point, however, it's
mostly just an example of how something like this could work and not a production-quality library. Use at your own risk.

### How does it work?

The basic idea is that we want to read or write the data as a byte[] since writing large amounts of data in bulk is very fast. To do this, we need to be able to trick the runtime into thinking
the data is in a different format than it is. It turns out that in the .NET CLR arrays get laid out on the heap like this:

![array-layout-clr.png](/array-layout-clr.png)

If you understood that diagram and are familiar with unsafe C#, then you know what we need to do :-). To convert a `byte[]` to a `float[]` we can do something like this:

```C#
float[] ConvertToFloat(byte[] bytes)
{
    fixed(byte* p = bytes)
    {
        IntPtr* p2 = (IntPtr*) p;
        p2[-1] = bytes.Length / 4; // each float is 4 bytes
        p2[-2] = /* pointer to the method table for a float[] */;
    }
    return (float[]) (object) bytes; // need to cast it twice so the C# compiler doesn't complain
}
```

To get the method table pointer for a float[], we can simply do the reverse:

```C#
IntPtr GetMethodTablePointerForFloatArray()
{
    float[] oneElemArray = new float[1]; // need a one-element array since fixed() on an empty array will yield a null pointer
    fixed(float* p = oneElemArray)
    {
        IntPtr* p2 = (IntPtr*) p;
        return p[-2];
    }
}
```

It would be nearly this easy except we can't do this with generics, since there's no way to get a `T*` for a generic `T` even if we do `where T : struct`. Oh, if only there were a `where T : fixed`... *sigh*.
So we need to use code generation and create some dynamic IL to do the proper casts.

Also, we also want to do any of the actual memory manipulation inside a [Constrained Execution Region](https://msdn.microsoft.com/en-us/library/ms228973%28v=vs.110%29.aspx?f=255&MSPPError=-2147217396)
so that something like a `ThreadAbortException` isn't thrown in between changing the size and the type.

Mono works a bit differently (there 2 additional size_t fields in between the size and the method table pointer), but the basic idea is the same.

There's one last tricky thing that only applies to `double[]`. Check out the [CLR code for allocating a double array on x86](https://github.com/dotnet/coreclr/blob/ef1e2ab328087c61a6878c1e84f4fc5d710aebce/src/vm/gchelpers.cpp#L610).
The comment there says it better than I ever could:

```C++
// Creation of an array of doubles, not in the large object heap. 
// We want to align the doubles to 8 byte boundaries, but the GC gives us pointers aligned 
// to 4 bytes only (on 32 bit platforms). To align, we ask for 12 bytes more to fill with a 
// dummy object. 
// If the GC gives us a 8 byte aligned address, we use it for the array and place the dummy 
// object after the array, otherwise we put the dummy object first, shifting the base of 
// the array to an 8 byte aligned address. 
// Note: on 64 bit platforms, the GC always returns 8 byte aligned addresses, and we don't 
// execute this code because DATA_ALIGNMENT < sizeof(double) is false. 
```

This appears to only be an issue with `double[]` and not an array of `struct S { double d; }`, even though they're exactly the same thing in memory. The workaround is to
make an aligned copy after converting.