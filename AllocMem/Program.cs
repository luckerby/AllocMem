﻿using System;
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
        private static readonly AutoResetEvent doneEvent = new AutoResetEvent(false);
        static void Main(string[] args)
        {
            var res = Parser.Default.ParseArguments<Options>(args)
                .WithParsed(RunOptions)
                .WithNotParsed(HandleParseError);
            
            // If Ctrl+C was pressed after the allocation completed then
            //  our custom CancelKeyPress event will make sure this point
            //  will be reached
        }

        static void RunOptions(Options opts)
        {
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
            if (blockSize > 8188)
            {
                Console.WriteLine("Input block size too large. Maximum allowed value is 8188 MB. Exiting");
                return;
            }
            
            // First we need to understand how many int elements we need inside our
            //  basic int[] building block. The int array will have an overhead of
            //  24 bytes (its sync block index, type object pointer and size field all
            //  take 3 x pointer size) on 64-bit. The block size in bytes needs to be
            //  divided by 4 (how long an int takes, regardless of platform) to arrive
            //  at the number of elements needed in one building block. Accounting for
            //  the overhead means getting rid of 6 elements that would total 24 bytes
            //  (at 4 bytes per int element). This will make the int[] block consume
            //  exactly the size specified in the input
            int noElementsPerBlock = (int)((long)blockSize * 1_048_576 / 4 - 6);

            // The limit for the number of elements in an int array is 0X7FEFFFFF (or
            //  2,146,435,071) as captured here https://docs.microsoft.com/en-us/dotnet/api/system.array?view=net-5.0#remarks
            //  Trying to allocate even 1 more element on top of that will result in
            //  an "Array dimensions exceeded supported range" exception. Putting this
            //  value in the noElementsPerBlock "equation" above yields a blockSize of
            //  8,188 MB which gives in turn the max value for the blockSize variable

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
            //  neglijible and we won't consider it in the computations, instead just print a line with the size

            // The size of memory that will be consumed by the List<int[]>
            //  as computed above
            int listSize = (noOfMemoryBlocksToAllocate + 6) * 8 + 8;
            Console.WriteLine("List<int[]> will consume {0} bytes", listSize);
            Console.WriteLine();

            int currentBlockNo = 0;
            // Start the loop that will be allocating memory
            do
            {
                // Delay the next allocation by the amount specified. Note
                //  that 0 won't cause any sort of delay. We do this here
                //  as opposed to the end of the loop as to not wait after
                //  the last block has been allocated
                if (currentBlockNo != 0)
                {
                    Thread.Sleep(delay);
                }

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
                Console.WriteLine("Block #{0}  +{1}MB (touched {2:f0}%)  [so far total allocated= {3}MB / total touched= {4:0.##}MB]",
                    currentBlockNo, blockSize, touchFillRatio*100, blockSize*(currentBlockNo+1),
                    blockSize*(currentBlockNo+1)*touchFillRatio);

                currentBlockNo++;
            } while (currentBlockNo < noOfMemoryBlocksToAllocate || allocateIndefinitely);
            Console.WriteLine("Allocating memory complete. Press Ctrl+C to exit");

            // Use an event to handle the case of the app running inside a container, as
            //  Console.ReadLine doesn't work there even if "docker start -i" is used
            Console.CancelKeyPress += ((sender, args) =>
            {
                Console.WriteLine("Ctrl+C pressed");
                // Signal the main thread to continue execution
                doneEvent.Set();
                // Don't terminate the current process, but instead allow to
                // graciously exit all methods, concluding with Main
                args.Cancel = true;
            });

            // Waiting for the Ctrl+C handler to be invoked, so the AutoResetEvent gets set
            doneEvent.WaitOne();

            // Keep a reference for when not in Debug mode, to keep the GC off
            //Console.WriteLine(memoryBlockList.Count);
            GC.KeepAlive(memoryBlockList);
        }
    }
}