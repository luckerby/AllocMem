using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace AllocMem
{
    class Program
    {
        private static readonly AutoResetEvent _closingEvent = new AutoResetEvent(false);
        static void Main(string[] args)
        {
            // === ArrayList ===
            // For n int values inside a pre-allocated ArrayList:
            //  - The internal array will consume n x <pointer size> (plus 2x pointer size for its own sync block index and type object pointer)
            //  - The boxed ints will take n x (2x pointer size + pointer size per int value)
            //  - 2x pointer size for the ArrayList itself (own sync block index and type object pointer)
            // Total: n x ptr_size + 2 x ptr_size + n x (2 x ptr_size + ptr_size) + 2 x ptr_size
            //        = n x 4 x ptr_size + 4 x ptr_size
            //
            // For x64, sized consumed is:
            //  - for 1,024 elements:        1,024 x 4 x 8 bytes + 4 x 8 bytes = 32,800
            //  - for 100,000 elements:      100,000 x 4 x 8 bytes + 4 x 8 bytes = 3,200,032
            //  - for 100,000,000 elements:  100,000,000 x 4 x 8 bytes + 4 x 8 bytes = 3,200,000,032
            //
            // Note that despite the fact that an int should take 4 bytes on either x86/x64, in 64-bit the field for
            //  the array elements takes 8 bytes for ArrayList as seen with dotMemory
            //
            // === List ===
            // For n int values inside a pre-allocated List<int>:
            //  - The internal array will consume n x 4 bytes, plus pointer size bytes for its size, plus
            //     2x pointer size for its own sync block index and type object pointer
            //  - 2x pointer size for the List itself (own sync block index and type object pointer) plus
            //     1x pointer size to hold the reference to the internal array plus 
            //     2x 4 bytes for 2 internal int fields (_size and _version) that belong to List<int>
            // Total: n x 4 + 3 x ptr_size + 3 x ptr_size + 8 bytes
            //        = n x 4 + 6 x ptr_size + 8 bytes
            //
            // For x64, sized consumed is:
            //  - for 1,024 elements:    1,024 x 4 bytes + 6 x 8 bytes + 8 bytes                   = 4,152
            //  - for 100,000 elements:  100,000 x 4 bytes + 6 x 8 bytes + 8 bytes                 = 400,056
            //  - for 100,000,000 elements:  100,000,000 x 4 bytes + 6 x 8 bytes + 8 bytes         = 400,000,056
            //  - for 1,000,000,000 elements:  1,000,000,000 x 4 bytes + 6 x 8 bytes + 8 bytes     = 4,000,000,056

            /*
            /// The number of elements
            int n = 1_000_000_000;
            // Time to sleep in ms between allocations of 25m elements
            int lag = 2000;

            float memToAllocate = (float)n / 1_073_741_824 * 4;
            Console.WriteLine("Allocating (committing) [{0:f2}GB] up front", memToAllocate);
            List<int> list = new List<int>(n);
            Random random = new Random(1);
            // Touch each entry so pages are allocated, which in turn will get WS up
            for (int i = 0; i < n; i++)
            {
                //list.Add(random.Next(10));
                // Use just the current indexer, as it'll be faster than generating a random number
                list.Add(i);
                if (i % 25_000_000 == 0 && i>0)
                {
                    float memAllocatedUntilNow = (float)i / 1_073_741_824 * 4;
                    Console.WriteLine("Sleeping {0}ms after touching [{1:f2}GB]...", lag, memAllocatedUntilNow);
                    Thread.Sleep(lag);
                }
            }

            // Just keep the liniar term, as the constant one will be neglijible when
            //  converting to GB. Divide first, as to not run into wraparound
            float memAllocated = (float)n/1_073_741_824 * 4;
            Console.WriteLine("Touch complete [{0:f2}GB]", memAllocated);
            Console.WriteLine("Press Ctrl+C to terminate...");
            */

            // === New code starts here ===
            int blockSize;              // Memory to allocate per block, in MB
            double touchFillRatio;         // The fill ratio for touching memory (between 0 and 1 inclusive)
            int delay;                  // The delay between allocations

            // hardcoded for now, but should be picked up from cmd args
            blockSize = 1024;   // remember the value is in MB
            touchFillRatio = 0.1;

            if(blockSize > 8378)
            {
                Console.WriteLine("Block size too large. Maximum allowed value is 8378 MB");
                return;
            }

            // First we need to understand how many int elements we need inside our
            //  basic int[] building block. The int array will have an overhead of
            //  24 bytes (its sync block index, type object pointer and size field all
            //  take 3 x pointer size) on 64-bit. The block size in bytes needs to be
            //  divided by 4 (how long an int takes, regardless of platform) to arrive
            //  at the number of elements needed in one building block. Accounting for
            //  the overhead means getting rid of 6 elements that would total 24 bytes
            //  (at 4 bytes per int element)
            int noElementsPerBlock = (int)((long)blockSize * 1_024_768 / 4 - 6);

            // The limit for the number of elements in an int array is 0X7FEFFFFF (or
            //  2,146,435,071) as captured here https://docs.microsoft.com/en-us/dotnet/api/system.array?view=net-5.0#remarks
            //  Trying to allocate even 1 more element on top of that will result in
            //  an "Array dimensions exceeded supported range" exception. Putting this
            //  value in the noElementsPerBlock "equation" above yields a blockSize of
            //  8,378 MB which gives in turn the max value for the blockSize variable

            // Next we build the block that we'll reuse
            int[] block = new int[noElementsPerBlock];

            // Now we'll touch the memory inside the block according to the touch fill ratio
            //  The page size on both 32-bit and 64-bit is 4KB under both Linux and Windows,
            //  and since arrays are allocated contiguously in memory, we just need to touch
            //  one int element for every 1024
            for (int i = 0; i < noElementsPerBlock * touchFillRatio; i += 1024)
            {
                block[i] = 0;
            }


            // Secondly we need to compute how many int arrays we need

            //List<List<int>> 
            List<int[]> my = new List<int[]>();

            //  ....We know that the overhead is 56 bytes on 64-bit (look in the comments above)
            //  ....We'll use the constructor that specified the upfront length, so we don't run into the copying-and-double
            //  approach that happens with the parameterless constructor

            // === End of new code ====

            // Set up a handler to handle exiting from the container, as
            //  using Console.ReadLine doesn't work even if "docker start -i" is used
            Console.CancelKeyPress += ((sender, args) =>
                {
                    Console.WriteLine("Exiting");
                    _closingEvent.Set();
                });

            _closingEvent.WaitOne();

            // Keep a reference for when not in Debug mode, to keep the GC off
            Console.WriteLine(block.Length);
        }
    }
}
