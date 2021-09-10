using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using CommandLine;

namespace AllocMem
{
    class Options
    {
        [Option('m', Required = false, Default = 1, HelpText = "Size of individual memory blocks to allocate in MBs")]
        public int SizeOfBlock { get; set; }
        [Option('f', Required = false, Default = 1, HelpText = "Touch fill ratio, how much of the committed memory gets touched per each memory block allocated")]
        public double TouchFillRatio { get; set; }
        [Option('x', Required = true, HelpText = "Stop allocating once memory committed reaches this value in MBs (use 0 to exhaust)")]
        public int MaxMemoryToCommit { get; set; }
        [Option('e', Required = false, Default = 0, HelpText = "Time between allocations in ms")]
        public int Delay { get; set; }
    }

    class Program
    {
        private static readonly AutoResetEvent _closingEvent = new AutoResetEvent(false);
        static void Main(string[] args)
        {
            var res = Parser.Default.ParseArguments<Options>(args)
                .WithParsed(RunOptions)
                .WithNotParsed(HandleParseError);
            Console.WriteLine("Parsed the args");
        }

        static void RunOptions(Options opts)
        {
            Console.WriteLine("Params parsed, press a key to continue");
            Console.ReadLine();
            // We're here if the argument parsing was successful. Call the
            //  method that allocates memory and supply the needed values as params
            LeakMemory(opts.SizeOfBlock, opts.TouchFillRatio, opts.Delay, opts.MaxMemoryToCommit);
        }
        static void HandleParseError(IEnumerable<Error> errs)
        {
            // We do nothing, and leave the default implementation
            //  of CommandLineParser to display the issues
        }

        static void LeakMemory(int blockSize, double touchFillRatio, int delay, int maxMemoryToCommit)
        {
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
            // For x64, size consumed is:
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
            if (blockSize > 8378)
            {
                Console.WriteLine("Input block size too large. Maximum allowed value is 8378 MB. Exiting");
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
            int noElementsPerBlock = (int)((long)blockSize * 1_048_576 / 4 - 6);

            // The limit for the number of elements in an int array is 0X7FEFFFFF (or
            //  2,146,435,071) as captured here https://docs.microsoft.com/en-us/dotnet/api/system.array?view=net-5.0#remarks
            //  Trying to allocate even 1 more element on top of that will result in
            //  an "Array dimensions exceeded supported range" exception. Putting this
            //  value in the noElementsPerBlock "equation" above yields a blockSize of
            //  8,378 MB which gives in turn the max value for the blockSize variable

            // The number of blocks that will be allocated. If the block size
            //  doesn't fit nicely inside the max limit, we'll just allocate one more
            int noOfMemoryBlocksToAllocate = maxMemoryToCommit != 0 ?
                (maxMemoryToCommit % blockSize == 0 ? maxMemoryToCommit / blockSize : 
                maxMemoryToCommit / blockSize + 1) : 0;
            bool allocateIndefinitely = maxMemoryToCommit == 0 ? true : false;
            Console.WriteLine("Will allocate {0} blocks of memory each consuming {1} MB, as to hit a limit of {2} MB",
                noOfMemoryBlocksToAllocate, blockSize, maxMemoryToCommit);

            // The list whose whole purpose is to keep the references of allocated
            //  blocks of int arrays. We'll use the constructor that specifies the
            //  upfront length, so we don't run into the copying-and-double
            //  approach that happens with the parameterless constructor
            List<int[]> memoryBlockList = new List<int[]>(noOfMemoryBlocksToAllocate);

            // For n references inside a pre-allocated List<int[]>:
            //  - The internal array will consume n x pointer size bytes, plus pointer size bytes for its size, plus
            //     2x pointer size for its own sync block index and type object pointer
            //  - 2x pointer size for the List itself (own sync block index and type object pointer) plus
            //     1x pointer size to hold the reference to the internal array plus 
            //     2x 4 bytes for 2 internal int fields (_size and _version) that belong to List<int>
            // Total: n x ptr_size + 3 x ptr_size + 3 x ptr_size + 8 bytes
            //        = (n + 6) x ptr_size + 8 bytes
            //
            // For x64, size consumed is:
            //  - for 1,000 elements:    1,006 x 8 bytes + 8 bytes                  = 8,056 bytes
            //  - for 100,000 elements:  100,006 x 8 bytes + 8 bytes                = 800,056 bytes
            //
            // So with a block with a minimum size of 1 MB, a list that will contain 100,000 elements - meaning
            //  ~100 GB of allocated memory - would consume itself less than 1 MB. As such the quantity is
            //  neglijible and we won't consider it in the computations, just print a line with the size

            // The size of memory that will be consumed by the List<int[]>
            //  as computed above
            int listSize = (noOfMemoryBlocksToAllocate + 6) * 8 + 8;
            Console.WriteLine("List<int[]> will consume {0} bytes", listSize);
            Console.WriteLine();

            int currentBlockNo = 0;
            // Start the loop that will be allocating memory
            do
            {
                // Next we build a memory block
                int[] block = new int[noElementsPerBlock];

                // Now we'll touch the memory inside the block according to the touch fill ratio
                //  The page size on both 32-bit and 64-bit is 4KB under both Linux and Windows,
                //  and since arrays are allocated contiguously in memory, we just need to touch
                //  one int element for every 1024
                for (int i = 0; i < noElementsPerBlock * touchFillRatio; i += 1024)
                {
                    block[i] = 0;
                }

                // Add the new memory block's reference to the list, so it's
                //  not garbage collected
                memoryBlockList.Add(block);

                // Print statistics for the current block
                Console.WriteLine("Block #{0}  +{1}MB (touched {2:f0}%)  [total allocated so far= {3}MB]", currentBlockNo,
                    blockSize, touchFillRatio*100, blockSize*(currentBlockNo+1));

                currentBlockNo++;
            } while (currentBlockNo < noOfMemoryBlocksToAllocate || allocateIndefinitely);
            Console.WriteLine("Allocating memory complete");
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
            Console.WriteLine(memoryBlockList.Count);
        }
    }
}