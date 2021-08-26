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
            //  - The internal array will consume n x 4 bytes, plus ptr_size bytes for its size, plus
            //     2x pointer size for its own sync block index and type object pointer
            //  - 2x pointer size for the List itself (own sync block index and type object pointer) plus
            //     1x pointer size to hold the reference to the internal array plus 
            //     2x 4 bytes for 2 internal int fields (_size and _version)
            // Total: n x 4 + 3 x ptr_size + 3 x ptr_size + 8 bytes
            //        = n x 4 + 6 x ptr_size + 8 bytes
            //
            // For x64, sized consumed is:
            //  - for 1,024 elements:    1,024 x 4 bytes + 6 x 8 bytes + 8 bytes                   = 4,152
            //  - for 100,000 elements:  100,000 x 4 bytes + 6 x 8 bytes + 8 bytes                 = 400,056
            //  - for 100,000,000 elements:  100,000,000 x 4 bytes + 6 x 8 bytes + 8 bytes         = 400,000,056
            //  - for 1,000,000,000 elements:  1,000,000,000 x 4 bytes + 6 x 8 bytes + 8 bytes     = 4,000,000,056



            /// The number of elements
            int n = 1_000_000_000;
            // Time to sleep in ms between allocations of 25m elements
            int lag = 2000;
            /*
                        // ArrayList
                        ArrayList arrayList = new ArrayList(n);
                        Random random = new Random(1);
                        for (int i = 0; i < n; i++)
                        {
                            arrayList.Add(random.Next(10));
                        }
            */

            // List<int>
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

            // Set up a handler to handle exiting from the container, as
            //  using Console.ReadLine doesn't work even if "docker start -i" is used
            Console.CancelKeyPress += ((sender, args) =>
                {
                    Console.WriteLine("Exiting");
                    _closingEvent.Set();
                });

            _closingEvent.WaitOne();

            // Keep a reference for when not in Debug mode, to keep the GC off
            Console.WriteLine(list.Count);
        }
    }
}
