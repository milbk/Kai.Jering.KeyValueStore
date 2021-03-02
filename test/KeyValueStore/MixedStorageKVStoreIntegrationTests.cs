using FASTER.core;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Jering.KeyValueStore.Tests
{
    /// <summary>
    /// Verifies behaviour of <see cref="IMixedStorageKVStore{TKey, TValue}"/> implementations and their underlying <see cref="FasterKV{TKey, TValue}"/> instances. They:
    /// <list type="bullet">
    /// <item>Handle concurrent insert, update, read and delete operations</item>
    /// <item>Handle reference-type and value-type keys and values</item>
    /// <item>Delete log files on dispose</item>
    /// <item>Compact logs periodically</item>
    /// </list>
    /// </summary>
    public class MixedStorageKVStoreIntegrationTests : IClassFixture<MixedStorageKVStoreIntegrationTestsFixture>
    {
        private const int TIMEOUT_MS = 60000;
        private readonly MixedStorageKVStoreIntegrationTestsFixture _fixture;

        public MixedStorageKVStoreIntegrationTests(MixedStorageKVStoreIntegrationTestsFixture fixture)
        {
            _fixture = fixture;
        }

        // TODO Interleave operations
        [Fact]
        public async Task UpsertReadAsyncDelete_AreThreadSafe()
        {
            // Arrange
            var dummyOptions = new MixedStorageKVStoreOptions()
            {
                LogDirectory = _fixture.TempDirectory,
                LogFileNamePrefix = nameof(UpsertReadAsyncDelete_AreThreadSafe),
                PageSizeBits = 12,
                MemorySizeBits = 13 // Limit to 8KB so we're testing both in-memory and disk-based operations
            };
            DummyClass dummyClassInstance = CreatePopulatedDummyClassInstance();
            int numRecords = 10000;
            //using var testSubject = new ObjLogMixedStorageKVStore<int, DummyClass>(dummyOptions);
            //using var testSubject = new MemoryMixedStorageKVStore<int, DummyClass>(dummyOptions);
            using var testSubject = new MixedStorageKVStore<int, DummyClass>(dummyOptions);

            // Act and assert

            // Insert
            Parallel.For(0, numRecords, key => testSubject.Upsert(key, dummyClassInstance));

            // Read
            await ReadAndVerifyValuesAsync(numRecords, testSubject, Status.OK, dummyClassInstance).ConfigureAwait(false);

            // Update
            dummyClassInstance.DummyInt = 20;
            dummyClassInstance.DummyString = "anotherDummyString";
            Parallel.For(0, numRecords, key => testSubject.Upsert(key, dummyClassInstance));

            // Verify updates
            await ReadAndVerifyValuesAsync(numRecords, testSubject, Status.OK, dummyClassInstance).ConfigureAwait(false);

            // Delete
            Parallel.For(0, numRecords, key => testSubject.Delete(key));

            // Verify deletes
            await ReadAndVerifyValuesAsync(numRecords, testSubject, Status.NOTFOUND, null).ConfigureAwait(false);
        }

        [Fact]
        public async Task KeysAndValues_SupportsObjects()
        {
            // Arrange
            var dummyOptions = new MixedStorageKVStoreOptions()
            {
                LogDirectory = _fixture.TempDirectory,
                LogFileNamePrefix = nameof(KeysAndValues_SupportsObjects),
                PageSizeBits = 12,
                MemorySizeBits = 13 // Limit to 8KB so we're testing both in-memory and disk-based operations
            };
            int numRecords = 10000;
            using var testSubject = new MixedStorageKVStore<string, string>(dummyOptions);

            // Act and assert

            // Insert
            Parallel.For(0, numRecords, key =>
            {
                string keyAsString = key.ToString();
                testSubject.Upsert(keyAsString, keyAsString);
            });

            // Read
            List<Task<(Status, string?)>> readTasks = new();
            for (int key = 0; key < numRecords; key++)
            {
                readTasks.Add(ReadAsync(key.ToString(), testSubject));
            }
            await Task.WhenAll(readTasks).ConfigureAwait(false);

            // Verify
            Parallel.For(0, numRecords, key =>
            {
                (Status status, string? result) = readTasks[key].Result;
                Assert.Equal(Status.OK, status);
                Assert.Equal(key.ToString(), result);
            });
        }

        [Fact]
        public async Task KeysAndValues_SupportsVariableLengthStructs()
        {
            // Arrange
            var dummyOptions = new MixedStorageKVStoreOptions()
            {
                LogDirectory = _fixture.TempDirectory,
                LogFileNamePrefix = nameof(KeysAndValues_SupportsVariableLengthStructs),
                PageSizeBits = 12,
                MemorySizeBits = 13 // Limit to 8KB so we're testing both in-memory and disk-based operations
            };
            var dummyStructInstance = new DummyVariableLengthStruct()
            {
                // Populate with dummy values
                DummyString = "dummyString",
                DummyStringArray = new[] { "dummyString1", "dummyString2", "dummyString3", "dummyString4", "dummyString5" },
                DummyIntArray = new[] { 10, 100, 1000, 10000, 100000, 1000000, 10000000 }
            };
            int numRecords = 10000;
            using var testSubject = new MixedStorageKVStore<DummyVariableLengthStruct, DummyVariableLengthStruct>(dummyOptions);

            // Act and assert

            // Insert
            Parallel.For(0, numRecords, key =>
            {
                DummyVariableLengthStruct localDummyStructInstance = dummyStructInstance;
                localDummyStructInstance.DummyInt = key;
                testSubject.Upsert(localDummyStructInstance, localDummyStructInstance);
            });

            // Read
            List<Task<(Status, DummyVariableLengthStruct)>> readTasks = new();
            for (int key = 0; key < numRecords; key++)
            {
                DummyVariableLengthStruct localDummyStructInstance = dummyStructInstance;
                localDummyStructInstance.DummyInt = key;
                readTasks.Add(ReadAsync(localDummyStructInstance, testSubject));
            }
            await Task.WhenAll(readTasks).ConfigureAwait(false);

            // Verify
            for (int key = 0; key < numRecords; key++)
            {
                (Status status, DummyVariableLengthStruct result) = readTasks[key].Result;
                Assert.Equal(Status.OK, status);
                DummyVariableLengthStruct localDummyStructInstance = dummyStructInstance;
                localDummyStructInstance.DummyInt = key;
                Assert.Equal(localDummyStructInstance, result);
            };
        }

        [Fact]
        public async Task KeysAndValues_SupportsFixedLengthStructs()
        {
            // Arrange
            var dummyOptions = new MixedStorageKVStoreOptions()
            {
                LogDirectory = _fixture.TempDirectory,
                LogFileNamePrefix = nameof(KeysAndValues_SupportsFixedLengthStructs),
                PageSizeBits = 12,
                MemorySizeBits = 13 // Limit to 8KB so we're testing both in-memory and disk-based operations
            };
            var dummyStructInstance = new DummyFixedLengthStruct()
            {
                // Populate with dummy values
                DummyByte = byte.MaxValue,
                DummyShort = short.MaxValue,
                DummyLong = long.MaxValue
            };
            int numRecords = 10000;
            using var testSubject = new MixedStorageKVStore<DummyFixedLengthStruct, DummyFixedLengthStruct>(dummyOptions);

            // Act and assert

            // Insert
            Parallel.For(0, numRecords, key =>
            {
                DummyFixedLengthStruct localDummyStructInstance = dummyStructInstance;
                localDummyStructInstance.DummyInt = key;
                testSubject.Upsert(localDummyStructInstance, localDummyStructInstance);
            });

            // Read
            List<Task<(Status, DummyFixedLengthStruct)>> readTasks = new();
            for (int key = 0; key < numRecords; key++)
            {
                DummyFixedLengthStruct localDummyStructInstance = dummyStructInstance;
                localDummyStructInstance.DummyInt = key;
                readTasks.Add(ReadAsync(localDummyStructInstance, testSubject));
            }
            await Task.WhenAll(readTasks).ConfigureAwait(false);

            // Verify
            for (int key = 0; key < numRecords; key++)
            {
                (Status status, DummyFixedLengthStruct result) = readTasks[key].Result;
                Assert.Equal(Status.OK, status);
                DummyFixedLengthStruct localDummyStructInstance = dummyStructInstance;
                localDummyStructInstance.DummyInt = key;
                Assert.Equal(localDummyStructInstance, result);
            };
        }

        [Fact]
        public async Task KeysAndValues_SupportsPrimitives()
        {
            // Arrange
            var dummyOptions = new MixedStorageKVStoreOptions()
            {
                LogDirectory = _fixture.TempDirectory,
                LogFileNamePrefix = nameof(KeysAndValues_SupportsPrimitives),
                PageSizeBits = 12,
                MemorySizeBits = 13 // Limit to 8KB so we're testing both in-memory and disk-based operations
            };
            const int dummyValue = 12345;
            const int numRecords = 10000;
            using var testSubject = new MixedStorageKVStore<int, int>(dummyOptions);

            // Act and assert
            Parallel.For(0, numRecords, key => testSubject.Upsert(key, dummyValue));
            await ReadAndVerifyValuesAsync(numRecords, testSubject, Status.OK, dummyValue).ConfigureAwait(false);
        }

        [Fact]
        public void LogFiles_DeletedOnClose()
        {
            // Arrange
            var directory = Path.Combine(_fixture.TempDirectory, nameof(LogFiles_DeletedOnClose)); // Use a separate directory so the test is never affected by other tests
            var dummyOptions = new MixedStorageKVStoreOptions()
            {
                LogDirectory = directory,
                LogFileNamePrefix = nameof(LogFiles_DeletedOnClose),
                PageSizeBits = 9, // Minimum
                MemorySizeBits = 10 // Minimum
            };
            DummyClass dummyClassInstance = CreatePopulatedDummyClassInstance();
            int numRecords = 50; // Just enough to make sure log files are created. Segment size isn't exceeded (only 1 of each log file).
            var testSubject = new MixedStorageKVStore<int, DummyClass>(dummyOptions);
            Parallel.For(0, numRecords, key => testSubject.Upsert(key, dummyClassInstance)); // Creates log
            Assert.Single(Directory.EnumerateFiles(directory, $"{nameof(LogFiles_DeletedOnClose)}*")); // Log and object log

            // Act
            testSubject.Dispose();

            // Assert
            Assert.Empty(Directory.EnumerateFiles(directory)); // Logs deleted
        }

        [Fact(Timeout = TIMEOUT_MS)]
        public async Task LogCompaction_SkippedIfSafeReadOnlyRegionIsLessThanThreshold()
        {
            // Arrange
            var resultStringBuilder = new StringBuilder();
            ILogger<MixedStorageKVStore<int, DummyClass>> dummyLogger = CreateLogger<int, DummyClass>(resultStringBuilder, LogLevel.Trace);
            var logSettings = new LogSettings
            {
                LogDevice = Devices.CreateLogDevice(Path.Combine(_fixture.TempDirectory, $"{nameof(LogCompaction_SkippedIfSafeReadOnlyRegionIsLessThanThreshold)}.log"),
                    deleteOnClose: true),
                PageSizeBits = 12,
                MemorySizeBits = 13
            };
            DummyClass dummyClassInstance = CreatePopulatedDummyClassInstance();
            int dummyThreshold = 100_000;
            var dummyOptions = new MixedStorageKVStoreOptions()
            {
                TimeBetweenLogCompactionsMS = 1,
                InitialLogCompactionThresholdBytes = 100_000
            };
            int expectedResultMinLength = string.Format(Strings.LogTrace_SkippingLogCompaction, 0, dummyThreshold).Length;

            // Act
            using (var testSubject = new MixedStorageKVStore<int, DummyClass>(dummyOptions, dummyLogger)) // Start log compaction
            {
                while (resultStringBuilder.Length <= expectedResultMinLength)
                {
                    await Task.Delay(10).ConfigureAwait(false);
                }
            }

            // Assert
            string result = resultStringBuilder.ToString();
            int numLines = result.Split("\n", StringSplitOptions.RemoveEmptyEntries).Length;
            string regexPattern = string.Format(Strings.LogTrace_SkippingLogCompaction, "0", dummyThreshold); // MixedStorageKVStore is empty
            Assert.Equal(numLines, Regex.Matches(result, regexPattern).Count);
        }

        [Fact(Timeout = TIMEOUT_MS)]
        public async Task LogCompaction_OccursIfSafeReadOnlyRegionIsLargerThanThreshold()
        {
            // Arrange
            var resultStringBuilder = new StringBuilder();
            ILogger<MixedStorageKVStore<int, DummyClass>> dummyLogger = CreateLogger<int, DummyClass>(resultStringBuilder, LogLevel.Trace);
            var logSettings = new LogSettings
            {
                LogDevice = Devices.CreateLogDevice(Path.Combine(_fixture.TempDirectory, $"{nameof(LogCompaction_OccursIfSafeReadOnlyRegionIsLargerThanThreshold)}.log"),
                    deleteOnClose: true),
                PageSizeBits = 12,
                MemorySizeBits = 13
            };
            DummyClass dummyClassInstance = CreatePopulatedDummyClassInstance();
            var dummyOptions = new MixedStorageKVStoreOptions()
            {
                TimeBetweenLogCompactionsMS = 1,
                InitialLogCompactionThresholdBytes = 80_000
            };
            // Create and populate faster KV store before passing it to MixedStorageKVStore, at which point the compaction loop starts.
            // For quicker tests, use thread local sessions.
            FasterKV<SpanByte, SpanByte>? dummyFasterKVStore = null;
            ThreadLocal<ClientSession<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, Empty, SpanByteFunctions<Empty>>>? dummyThreadLocalSession = null;
            try
            {
                dummyFasterKVStore = new FasterKV<SpanByte, SpanByte>(1L << 20, logSettings);
                FasterKV<SpanByte, SpanByte>.ClientSessionBuilder<SpanByte, SpanByteAndMemory, Empty> dummyClientSessionBuilder = dummyFasterKVStore.For(new SpanByteFunctions<Empty>());
                dummyThreadLocalSession = new(() => dummyClientSessionBuilder.NewSession<SpanByteFunctions<Empty>>(), true);
                MessagePackSerializerOptions dummyMessagePackSerializerOptions = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray);

                // Record size estimate:
                // - dummyClassInstance serialized and compressed = ~73 bytes
                // - int key serialized and compressed = ~3 bytes
                // - key length metadata = 4 bytes
                // - value length metadata = 4 bytes
                // - record header = 8 bytes
                // Total = ~92 bytes
                //
                // n * 92 - 8192 > InitialLogCompactionThresholdBytes

                // Insert
                byte[] dummyValueBytes = MessagePackSerializer.Serialize(dummyClassInstance, dummyMessagePackSerializerOptions);
                Parallel.For(0, 500, key =>
                {
                    byte[] dummyKeyBytes = MessagePackSerializer.Serialize(key, dummyMessagePackSerializerOptions);
                    unsafe
                    {
                        // Upsert
                        fixed (byte* keyPointer = dummyKeyBytes)
                        fixed (byte* valuePointer = dummyValueBytes)
                        {
                            var keySpanByte = SpanByte.FromPointer(keyPointer, dummyKeyBytes.Length);
                            var objSpanByte = SpanByte.FromPointer(valuePointer, dummyValueBytes.Length);
                            dummyThreadLocalSession.Value.Upsert(ref keySpanByte, ref objSpanByte);
                        }
                    }
                });
                // Update so compaction does something. Can't update in insert loop or we'll get a bunch of in-place updates.
                dummyClassInstance.DummyInt++;
                dummyValueBytes = MessagePackSerializer.Serialize(dummyClassInstance, dummyMessagePackSerializerOptions);
                Parallel.For(0, 500, key =>
                {
                    byte[] dummyKeyBytes = MessagePackSerializer.Serialize(key, dummyMessagePackSerializerOptions);
                    unsafe
                    {
                        // Upsert
                        fixed (byte* keyPointer = dummyKeyBytes)
                        fixed (byte* valuePointer = dummyValueBytes)
                        {
                            var keySpanByte = SpanByte.FromPointer(keyPointer, dummyKeyBytes.Length);
                            var objSpanByte = SpanByte.FromPointer(valuePointer, dummyValueBytes.Length);
                            dummyThreadLocalSession.Value.Upsert(ref keySpanByte, ref objSpanByte);
                        }
                    }
                });
            }
            finally
            {
                if (dummyThreadLocalSession != null)
                {
                    foreach (ClientSession<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, Empty, SpanByteFunctions<Empty>> session in dummyThreadLocalSession.Values)
                    {
                        session.Dispose(); // Faster synchronously completes all pending operations, so we should not get exceptions if we're in the middle of log compaction
                    }
                }
            }

            // We compact 20% of the safe readonly region of the log. Since we inserted then updated, compaction here means removal.
            // 90048 * 0.8 = 72038, ~72000 (missing 38 bytes likely has to do with the fact that we can't remove only part of a record). 
            string expectedResultStart = $"{LogLevel.Trace}: {string.Format(Strings.LogTrace_LogCompacted, 90048, 72000, 1)}";
            int expectedResultStartLength = expectedResultStart.Length;

            // Act
            using (var testSubject = new MixedStorageKVStore<int, DummyClass>(dummyOptions, dummyLogger, dummyFasterKVStore)) // Start log compaction
            {
                while (resultStringBuilder.Length <= expectedResultStartLength)
                {
                    await Task.Delay(10).ConfigureAwait(false);
                }
            }

            // Assert
            Assert.StartsWith(expectedResultStart, resultStringBuilder.ToString());
            // If compaction runs more than once, should be skipped after first compaction (< threshold behaviour verified in previous test)
        }

        [Fact(Timeout = TIMEOUT_MS)]
        public async Task LogCompaction_IncreasesThresholdAfterFiveConsecutiveCompactions()
        {
            // Arrange
            var resultStringBuilder = new StringBuilder();
            ILogger<MixedStorageKVStore<int, DummyClass>> dummyLogger = CreateLogger<int, DummyClass>(resultStringBuilder, LogLevel.Trace);
            var logSettings = new LogSettings
            {
                LogDevice = Devices.CreateLogDevice(Path.Combine(_fixture.TempDirectory, $"{nameof(LogCompaction_IncreasesThresholdAfterFiveConsecutiveCompactions)}.log"),
                    deleteOnClose: true),
                PageSizeBits = 12,
                MemorySizeBits = 13
            };
            DummyClass dummyClassInstance = CreatePopulatedDummyClassInstance();
            var dummyOptions = new MixedStorageKVStoreOptions()
            {
                TimeBetweenLogCompactionsMS = 1,
                InitialLogCompactionThresholdBytes = 20_000 // So we compact 5 times in a row
            };
            // Create and populate faster KV store before passing it to MixedStorageKVStore, at which point the compaction loop starts.
            // For quicker tests, use thread local sessions.
            FasterKV<SpanByte, SpanByte>? dummyFasterKVStore = null;
            ThreadLocal<ClientSession<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, Empty, SpanByteFunctions<Empty>>>? dummyThreadLocalSession = null;
            try
            {
                dummyFasterKVStore = new FasterKV<SpanByte, SpanByte>(1L << 20, logSettings);
                FasterKV<SpanByte, SpanByte>.ClientSessionBuilder<SpanByte, SpanByteAndMemory, Empty> dummyClientSessionBuilder = dummyFasterKVStore.For(new SpanByteFunctions<Empty>());
                dummyThreadLocalSession = new(() => dummyClientSessionBuilder.NewSession<SpanByteFunctions<Empty>>(), true);
                MessagePackSerializerOptions dummyMessagePackSerializerOptions = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray);

                // Record size estimate:
                // - dummyClassInstance serialized and compressed = ~73 bytes
                // - int key serialized and compressed = ~3 bytes
                // - key length metadata = 4 bytes
                // - value length metadata = 4 bytes
                // - record header = 8 bytes
                // Total = ~92 bytes
                //
                // n * 92 - 8192 > InitialLogCompactionThresholdBytes

                // Insert
                byte[] dummyValueBytes = MessagePackSerializer.Serialize(dummyClassInstance, dummyMessagePackSerializerOptions);
                Parallel.For(0, 500, key =>
                {
                    byte[] dummyKeyBytes = MessagePackSerializer.Serialize(key, dummyMessagePackSerializerOptions);
                    unsafe
                    {
                        // Upsert
                        fixed (byte* keyPointer = dummyKeyBytes)
                        fixed (byte* valuePointer = dummyValueBytes)
                        {
                            var keySpanByte = SpanByte.FromPointer(keyPointer, dummyKeyBytes.Length);
                            var objSpanByte = SpanByte.FromPointer(valuePointer, dummyValueBytes.Length);
                            dummyThreadLocalSession.Value.Upsert(ref keySpanByte, ref objSpanByte);
                        }
                    }
                });
                // Update so compaction does something. Can't update in insert loop or we'll get a bunch of in-place updates.
                dummyClassInstance.DummyInt++;
                dummyValueBytes = MessagePackSerializer.Serialize(dummyClassInstance, dummyMessagePackSerializerOptions);
                Parallel.For(0, 500, key =>
                {
                    byte[] dummyKeyBytes = MessagePackSerializer.Serialize(key, dummyMessagePackSerializerOptions);
                    unsafe
                    {
                        // Upsert
                        fixed (byte* keyPointer = dummyKeyBytes)
                        fixed (byte* valuePointer = dummyValueBytes)
                        {
                            var keySpanByte = SpanByte.FromPointer(keyPointer, dummyKeyBytes.Length);
                            var objSpanByte = SpanByte.FromPointer(valuePointer, dummyValueBytes.Length);
                            dummyThreadLocalSession.Value.Upsert(ref keySpanByte, ref objSpanByte);
                        }
                    }
                });
            }
            finally
            {
                if (dummyThreadLocalSession != null)
                {
                    foreach (ClientSession<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, Empty, SpanByteFunctions<Empty>> session in dummyThreadLocalSession.Values)
                    {
                        session.Dispose(); // Faster synchronously completes all pending operations, so we should not get exceptions if we're in the middle of log compaction
                    }
                }
            }

            // Runs 5 consecutive compactions, increases threshold, runs 5 more consecutive compactions (all redundant), increases threshold above 
            // safe readonly region size, skips compactions thereafter.
            string expectedResultStart = @$"{LogLevel.Trace}: {string.Format(Strings.LogTrace_LogCompacted, 90048, 72000, 1)}
{LogLevel.Trace}: {string.Format(Strings.LogTrace_LogCompacted, 72000, 57600, 2)}
{LogLevel.Trace}: {string.Format(Strings.LogTrace_LogCompacted, 57600, 46080, 3)}
{LogLevel.Trace}: {string.Format(Strings.LogTrace_LogCompacted, 46080, 40960, 4)}
{LogLevel.Trace}: {string.Format(Strings.LogTrace_LogCompacted, 40960, 40960, 5)}
{LogLevel.Trace}: {string.Format(Strings.LogTrace_LogCompactionThresholdIncreased, 20000, 40000)}
{LogLevel.Trace}: {string.Format(Strings.LogTrace_LogCompacted, 40960, 40960, 1)}
{LogLevel.Trace}: {string.Format(Strings.LogTrace_LogCompacted, 40960, 40960, 2)}
{LogLevel.Trace}: {string.Format(Strings.LogTrace_LogCompacted, 40960, 40960, 3)}
{LogLevel.Trace}: {string.Format(Strings.LogTrace_LogCompacted, 40960, 40960, 4)}
{LogLevel.Trace}: {string.Format(Strings.LogTrace_LogCompacted, 40960, 40960, 5)}
{LogLevel.Trace}: {string.Format(Strings.LogTrace_LogCompactionThresholdIncreased, 40000, 80000)}
{LogLevel.Trace}: {string.Format(Strings.LogTrace_SkippingLogCompaction, 40960, 80000)}";
            int expectedResultStartLength = expectedResultStart.Length;

            // Act
            using (var testSubject = new MixedStorageKVStore<int, DummyClass>(dummyOptions, dummyLogger, dummyFasterKVStore)) // Start log compaction
            {
                while (resultStringBuilder.Length <= expectedResultStartLength)
                {
                    await Task.Delay(10).ConfigureAwait(false);
                }
            }

            // Assert
            Assert.StartsWith(expectedResultStart, resultStringBuilder.ToString().Replace("\r\n", "\n"));
        }

        #region Helpers
        private static DummyClass CreatePopulatedDummyClassInstance()
        {
            return new DummyClass()
            {
                // Populate with dummy values
                DummyString = "dummyString",
                DummyStringArray = new[] { "dummyString1", "dummyString2", "dummyString3", "dummyString4", "dummyString5" },
                DummyInt = 10,
                DummyIntArray = new[] { 10, 100, 1000, 10000, 100000, 1000000, 10000000 }
            };
        }

        private static ILogger<MixedStorageKVStore<TKey, TValue>> CreateLogger<TKey, TValue>(StringBuilder stringBuilder, LogLevel minLogLevel)
        {
            var services = new ServiceCollection();
            services.AddLogging(lb => lb.
                AddProvider(new StringBuilderProvider(stringBuilder)).
                AddFilter<StringBuilderProvider>(logLevel => logLevel >= minLogLevel));

            ServiceProvider serviceProvider = services.BuildServiceProvider();

            return serviceProvider.GetRequiredService<ILogger<MixedStorageKVStore<TKey, TValue>>>();
        }

        private static async Task ReadAndVerifyValuesAsync<TValue>(int numRecords,
            IMixedStorageKVStore<int, TValue> MixedStorageKVStore,
            Status expectedStatus,
            TValue? expectedResult)
        {
            // Read
            List<Task<(Status, TValue?)>> readTasks = new();
            for (int key = 0; key < numRecords; key++)
            {
                readTasks.Add(ReadAsync(key, MixedStorageKVStore));
            }
            await Task.WhenAll(readTasks).ConfigureAwait(false);

            // Verify
            Parallel.For(0, numRecords, index =>
            {
                (Status status, TValue? result) = readTasks[index].Result;
                Assert.Equal(expectedStatus, status);
                Assert.Equal(expectedResult, result);
            });
        }

        private static async Task<(Status, TValue?)> ReadAsync<TKey, TValue>(TKey key, IMixedStorageKVStore<TKey, TValue> MixedStorageKVStore)
        {
            // Parallel.For doesn't await async actions, so we use Task.Yield to ensure operations run completely asynchronously and complete as
            // quickly as possible.
            await Task.Yield();

            return await MixedStorageKVStore.ReadAsync(key).ConfigureAwait(false);
        }
        #endregion

        #region Types
        [MessagePackObject]
        public struct DummyFixedLengthStruct
        {
            [Key(0)]
            public byte DummyByte { get; set; }

            [Key(1)]
            public short DummyShort { get; set; }

            [Key(2)]
            public int DummyInt { get; set; }

            [Key(3)]
            public long DummyLong { get; set; }

            public override bool Equals(object? obj)
            {
                if (obj is not DummyFixedLengthStruct castObject)
                {
                    return false;
                }

                return castObject.DummyByte == DummyByte &&
                    castObject.DummyShort == DummyShort &&
                    castObject.DummyInt == DummyInt &&
                    castObject.DummyLong == DummyLong;
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(DummyByte, DummyShort, DummyInt, DummyLong);
            }
        }

        [MessagePackObject]
        public struct DummyVariableLengthStruct
        {
            [Key(0)]
            public string? DummyString { get; set; }

            [Key(1)]
            public string[]? DummyStringArray { get; set; }

            [Key(2)]
            public int DummyInt { get; set; }

            [Key(3)]
            public int[]? DummyIntArray { get; set; }

            public override bool Equals(object? obj)
            {
                if (obj is not DummyVariableLengthStruct castObject)
                {
                    return false;
                }

                return castObject.DummyString == DummyString &&
                    castObject.DummyInt == DummyInt &&
#pragma warning disable CS8604 // Arrays should never be null in these tests, throw if they are
                    castObject.DummyStringArray.SequenceEqual(DummyStringArray) &&
                    castObject.DummyIntArray.SequenceEqual(DummyIntArray);
#pragma warning restore CS8604
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(DummyString, DummyStringArray, DummyInt, DummyIntArray);
            }
        }

        [MessagePackObject]
        public class DummyClass
        {
            [Key(0)]
            public string? DummyString { get; set; }

            [Key(1)]
            public string[]? DummyStringArray { get; set; }

            [Key(2)]
            public int DummyInt { get; set; }

            [Key(3)]
            public int[]? DummyIntArray { get; set; }

            public override bool Equals(object? obj)
            {
                if (obj is not DummyClass castObject)
                {
                    return false;
                }

                return castObject.DummyString == DummyString &&
                    castObject.DummyInt == DummyInt &&
#pragma warning disable CS8604 // Arrays should never be null in these tests, throw if they are
                    castObject.DummyStringArray.SequenceEqual(DummyStringArray) &&
                    castObject.DummyIntArray.SequenceEqual(DummyIntArray);
#pragma warning restore CS8604
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(DummyString, DummyStringArray, DummyInt, DummyIntArray);
            }
        }
        #endregion
    }

    public class MixedStorageKVStoreIntegrationTestsFixture : IDisposable
    {
        public string TempDirectory { get; } = Path.Combine(Path.GetTempPath(), nameof(MixedStorageKVStoreIntegrationTests));

        public MixedStorageKVStoreIntegrationTestsFixture()
        {
            TryDeleteDirectory();
            Directory.CreateDirectory(TempDirectory);
        }

        private void TryDeleteDirectory()
        {
            try
            {
                Directory.Delete(TempDirectory, true);
            }
            catch
            {
                // Do nothing
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool _)
        {
            TryDeleteDirectory();
        }
    }
}