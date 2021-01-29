using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Rebus.Extensions;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.MySql.Tests.Timeouts;
using Rebus.MySql.Transport;
using Rebus.Tests.Contracts;
using Rebus.Threading.TaskParallelLibrary;
using Rebus.Transport;

namespace Rebus.MySql.Tests.Transport
{
    [TestFixture, Category(Categories.MySql)]
    public class TestMySqlTransport : FixtureBase
    {
        readonly string _tableName = "messages" + TestConfig.Suffix;
        MySqlTransport _transport;
        CancellationToken _cancellationToken;
        const string QueueName = "input";

        [SetUp]
        protected override void SetUp()
        {
            MySqlTestHelper.DropTableIfExists(_tableName);
            var consoleLoggerFactory = new ConsoleLoggerFactory(false);
            var asyncTaskFactory = new TplAsyncTaskFactory(consoleLoggerFactory);
            var connectionHelper = new MySqlConnectionHelper(MySqlTestHelper.ConnectionString);
            _transport = new MySqlTransport(connectionHelper, _tableName, QueueName, consoleLoggerFactory, asyncTaskFactory, new FakeRebusTime());
            _transport.EnsureTableIsCreated();

            Using(_transport);

            _transport.Initialize();
            _cancellationToken = new CancellationTokenSource().Token;

        }

        [Test]
        public async Task ReceivesSentMessageWhenTransactionIsCommitted()
        {
            using (var scope = new RebusTransactionScope())
            {
                await _transport.Send(QueueName, RecognizableMessage(), AmbientTransactionContext.Current);

                await scope.CompleteAsync();
            }

            TransportMessage transportMessage;
            using (var scope = new RebusTransactionScope())
            {
                transportMessage = await _transport.Receive(AmbientTransactionContext.Current, _cancellationToken);

                await scope.CompleteAsync();
            }

            AssertMessageIsRecognized(transportMessage);
        }

        [Test]
        public async Task CommitSentMessageToStorage()
        {
            using (var scope = new RebusTransactionScope())
            {
                await _transport.Send(QueueName, RecognizableMessage(), AmbientTransactionContext.Current);
                await scope.CompleteAsync();
            }

            Assert.That(true, Is.True);
        }

        [Test]
        public async Task DoesNotReceiveSentMessageWhenTransactionIsNotCommitted()
        {
            using (var scope = new RebusTransactionScope())
            {
                await _transport.Send(QueueName, RecognizableMessage(), AmbientTransactionContext.Current);

                //await context.Complete();
            }

            using (var scope = new RebusTransactionScope())
            {
                var transportMessage = await _transport.Receive(AmbientTransactionContext.Current, _cancellationToken);

                Assert.That(transportMessage, Is.Null);
            }
        }


        [TestCase(10)]
        public async Task LotsOfAsyncStuffGoingDown(int numberOfMessages)
        {
            var receivedMessages = 0;
            var messageIds = new ConcurrentDictionary<int, int>();

            Console.WriteLine("Sending {0} messages", numberOfMessages);

            await Task.WhenAll(Enumerable.Range(0, numberOfMessages)
                .Select(async i =>
                {
                    using (var scope = new RebusTransactionScope())
                    {
                        await _transport.Send(QueueName, RecognizableMessage(i), AmbientTransactionContext.Current);
                        await scope.CompleteAsync();

                        messageIds[i] = 0;
                    }
                }));

            Console.WriteLine("Receiving {0} messages", numberOfMessages);

            using (var timer = new Timer((object o) =>
            {
                Console.WriteLine("Received: {0} msgs", receivedMessages);
            }, null, TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(1000)))
            {
                await Task.WhenAll(Enumerable.Range(0, numberOfMessages)
                    .Select(async i =>
                    {
                        using (var scope = new RebusTransactionScope())
                        {
                            var msg = await _transport.Receive(AmbientTransactionContext.Current, _cancellationToken);
                            await scope.CompleteAsync();

                            Interlocked.Increment(ref receivedMessages);

                            var id = int.Parse(msg.Headers["id"]);

                            messageIds.AddOrUpdate(id, 1, (_, existing) => existing + 1);
                        }
                    }));

                await Task.Delay(1000);
            }

            Assert.That(messageIds.Keys.OrderBy(k => k).ToArray(), Is.EqualTo(Enumerable.Range(0, numberOfMessages).ToArray()));

            var kvpsDifferentThanOne = messageIds.Where(kvp => kvp.Value != 1).ToList();

            if (kvpsDifferentThanOne.Any())
            {
                Assert.Fail(@"Oh no! the following IDs were not received exactly once:
{0}",
    string.Join(Environment.NewLine, kvpsDifferentThanOne.Select(kvp => $"   {kvp.Key}: {kvp.Value}")));
            }
        }

        void AssertMessageIsRecognized(TransportMessage transportMessage)
        {
            Assert.That(transportMessage, Is.Not.Null);
            Assert.That(transportMessage.Headers.GetValue("recognizzle"), Is.EqualTo("hej"));
        }


        static TransportMessage RecognizableMessage(int id = 0)
        {
            var headers = new Dictionary<string, string>
            {
                {"recognizzle", "hej"},
                {"id", id.ToString()}
            };
            return new TransportMessage(headers, new byte[] { 1, 2, 3 });
        }
    }
}