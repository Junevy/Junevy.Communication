using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Factory;
using System.Collections.Concurrent;

namespace Junevy.Communication.Modbus.Tests
{
    /// <summary>
    /// 连接管理器别名语义验证：移除主连接时同时移除所有指向它的别名并释放实例
    /// （此前实例既不释放也不可达，串口/套接字泄漏）；RegisterAlias 拒绝悬空目标；
    /// RemoveAlias 对直接实例不再"删除后放回"。
    /// </summary>
    public class ConnectionManagerAliasTests
    {
        [Fact]
        public void TryRemove_MasterWithAlias_DisposesInstanceAndRemovesAlias()
        {
            var manager = new ModbusConnectionManager();
            var instance = new TrackedModbus();
            Assert.True(manager.Add("m", instance));
            Assert.True(manager.RegisterAlias("a", "m"));

            Assert.True(manager.TryRemove("m"));

            Assert.Equal(1, instance.DisposeCount);
            Assert.Null(manager.Get("a"));
            Assert.Empty(manager.Keys);
            Assert.Equal(0, manager.Count);
        }

        [Fact]
        public void TryRemove_MasterWithAliasChain_RemovesWholeChain()
        {
            var manager = new ModbusConnectionManager();
            var instance = new TrackedModbus();
            Assert.True(manager.Add("m", instance));
            Assert.True(manager.RegisterAlias("a", "m"));
            Assert.True(manager.RegisterAlias("b", "a"));

            Assert.True(manager.TryRemove("m"));

            Assert.Empty(manager.Keys);
            Assert.Equal(1, instance.DisposeCount);
        }

        [Fact]
        public void TryRemove_Alias_KeepsInstance()
        {
            var manager = new ModbusConnectionManager();
            var instance = new TrackedModbus();
            Assert.True(manager.Add("m", instance));
            Assert.True(manager.RegisterAlias("a", "m"));

            Assert.True(manager.TryRemove("a"));

            Assert.Same(instance, manager.Get("m"));
            Assert.Equal(0, instance.DisposeCount);
        }

        [Fact]
        public void TryRemove_SameInstanceUnderTwoDirectKeys_DoesNotDispose()
        {
            var manager = new ModbusConnectionManager();
            var instance = new TrackedModbus();
            Assert.True(manager.Add("m1", instance));
            Assert.True(manager.Add("m2", instance));

            Assert.True(manager.TryRemove("m1"));

            Assert.Equal(0, instance.DisposeCount);
            Assert.Same(instance, manager.Get("m2"));
        }

        [Fact]
        public void RegisterAlias_MissingTarget_ReturnsFalse()
        {
            var manager = new ModbusConnectionManager();

            Assert.False(manager.RegisterAlias("a", "nope"));
            Assert.Empty(manager.Keys);
        }

        [Fact]
        public void RegisterAlias_SameKey_ReturnsFalse()
        {
            var manager = new ModbusConnectionManager();
            Assert.True(manager.Add("m", new TrackedModbus()));

            Assert.False(manager.RegisterAlias("m", "m"));
        }

        [Fact]
        public void RegisterAlias_ToAlias_Resolves()
        {
            var manager = new ModbusConnectionManager();
            var instance = new TrackedModbus();
            Assert.True(manager.Add("m", instance));
            Assert.True(manager.RegisterAlias("a", "m"));

            Assert.True(manager.RegisterAlias("b", "a"));
            Assert.Same(instance, manager.Get("b"));
        }

        [Fact]
        public void RemoveAlias_DirectEntry_ReturnsFalseAndKeepsEntry()
        {
            var manager = new ModbusConnectionManager();
            var instance = new TrackedModbus();
            Assert.True(manager.Add("m", instance));

            Assert.False(manager.RemoveAlias("m"));
            Assert.Same(instance, manager.Get("m"));
        }

        [Fact]
        public void Dispose_AfterRemovingMasterWithAlias_LeavesNoLeak()
        {
            var manager = new ModbusConnectionManager();
            var instance = new TrackedModbus();
            Assert.True(manager.Add("m", instance));
            Assert.True(manager.RegisterAlias("a", "m"));
            Assert.True(manager.TryRemove("m"));

            manager.Dispose();

            Assert.Equal(1, instance.DisposeCount);
        }

        [Fact]
        public async Task Concurrent_AddAliasRemove_PreservesInvariants()
        {
            var manager = new ModbusConnectionManager();
            var keys = new[] { "k0", "k1", "k2", "k3" };
            int totalCreated = 0;
            var instances = new ConcurrentBag<TrackedModbus>();

            var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                var random = Random.Shared;
                for (int i = 0; i < 500; i++)
                {
                    switch (random.Next(4))
                    {
                        case 0:
                            var instance = new TrackedModbus();
                            Interlocked.Increment(ref totalCreated);
                            instances.Add(instance);
                            if (!manager.Add(keys[random.Next(4)], instance))
                                instance.Dispose(); // 键已存在：测试自行释放
                            break;
                        case 1:
                            manager.RegisterAlias("a" + random.Next(4), keys[random.Next(4)]);
                            break;
                        case 2:
                            manager.TryRemove(keys[random.Next(4)]);
                            break;
                        case 3:
                            manager.RemoveAlias("a" + random.Next(4));
                            break;
                    }
                }
            }));

            await Task.WhenAll(workers);

            // 不变量 1：Keys 中每个键都必须可解析（无悬空别名）
            foreach (var key in manager.Keys)
                Assert.NotNull(manager.Get(key));

            // 不变量 2：创建总数 - 已释放数 == 存活数 == 管理器直接条目数
            int disposedCount = instances.Sum(instance => instance.DisposeCount);
            Assert.Equal(totalCreated - disposedCount, manager.Count);
        }

        /// <summary>仅跟踪 Dispose 的 IModbus 桩。</summary>
        private sealed class TrackedModbus : IModbus
        {
            public int DisposeCount { get; private set; }

            public void Dispose() => DisposeCount++;

            public ModbusProtocolType ProtocolType => throw new NotSupportedException();

            public bool IsConnected => throw new NotSupportedException();

            public bool Connect() => throw new NotSupportedException();

            public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public void Disconnect() => throw new NotSupportedException();

            public ModbusResult<byte[]> Request(ModbusRequest tx) => throw new NotSupportedException();

            public Task<ModbusResult<byte[]>> RequestAsync(ModbusRequest tx, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();
        }
    }
}
