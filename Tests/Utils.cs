﻿// This source code is dual-licensed under the Apache License, version
// 2.0, and the Mozilla Public License, version 2.0.
// Copyright (c) 2007-2020 VMware, Inc.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.Stream.Client;
using RabbitMQ.Stream.Client.AMQP;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Tests
{
    public class Utils<TResult>
    {
        private readonly ITestOutputHelper testOutputHelper;

        public Utils(ITestOutputHelper testOutputHelper)
        {
            this.testOutputHelper = testOutputHelper;
        }

        public void WaitUntilTaskCompletes(TaskCompletionSource<TResult> tasks)
        {
            WaitUntilTaskCompletes(tasks, true, TimeSpan.FromSeconds(10));
        }

        public void WaitUntilTaskCompletes(TaskCompletionSource<TResult> tasks, bool expectToComplete = true)
        {
            WaitUntilTaskCompletes(tasks, expectToComplete, TimeSpan.FromSeconds(10));
        }

        public void WaitUntilTaskCompletes(TaskCompletionSource<TResult> tasks,
            bool expectToComplete,
            TimeSpan timeOut)
        {
            try
            {
                var resultTestWait = tasks.Task.Wait(timeOut);
                Assert.Equal(resultTestWait, expectToComplete);
            }
            catch (Exception e)
            {
                testOutputHelper.WriteLine($"wait until task completes error #{e}");
                throw;
            }
        }
    }

    public static class SystemUtils
    {
        // Waits for 10 seconds total by default
        public static void WaitUntil(Func<bool> func, ushort retries = 40)
        {
            while (!func())
            {
                Wait(TimeSpan.FromMilliseconds(250));
                --retries;
                if (retries == 0)
                {
                    throw new XunitException("timed out waiting on a condition!");
                }
            }
        }

        public static async void WaitUntilAsync(Func<Task<bool>> func, ushort retries = 10)
        {
            Wait();
            while (!await func())
            {
                Wait();
                --retries;
                if (retries == 0)
                {
                    throw new XunitException("timed out waiting on a condition!");
                }
            }
        }

        public static void Wait()
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(500));
        }

        public static void Wait(TimeSpan wait)
        {
            Thread.Sleep(wait);
        }

        public static void InitStreamSystemWithRandomStream(out StreamSystem system, out string stream,
            string clientProviderNameLocator = "stream-locator")
        {
            stream = Guid.NewGuid().ToString();
            var config = new StreamSystemConfig
            {
                ClientProvidedName = clientProviderNameLocator
            };
            system = StreamSystem.Create(config).Result;
            var x = system.CreateStream(new StreamSpec(stream));
            x.Wait();
        }

        public static async Task PublishMessages(StreamSystem system, string stream, int numberOfMessages,
            ITestOutputHelper testOutputHelper)
        {
            await PublishMessages(system, stream, numberOfMessages, "producer", testOutputHelper);
        }

        public static async Task PublishMessages(StreamSystem system, string stream, int numberOfMessages,
            string producerName, ITestOutputHelper testOutputHelper)
        {
            var testPassed = new TaskCompletionSource<int>();
            var count = 0;
            var producer = await system.CreateProducer(
                new ProducerConfig
                {
                    Reference = producerName,
                    Stream = stream,
                    ConfirmHandler = _ =>
                    {
                        count++;
                        testOutputHelper.WriteLine($"Published and Confirmed: {count} messages");
                        if (count != numberOfMessages)
                        {
                            return;
                        }

                        testPassed.SetResult(count);
                    }
                });

            for (var i = 0; i < numberOfMessages; i++)
            {
                var msgData = new Data($"message_{i}".AsReadonlySequence());
                var message = new Message(msgData);
                await producer.Send(Convert.ToUInt64(i), message);
            }

            testPassed.Task.Wait(TimeSpan.FromSeconds(10));
            Assert.Equal(producer.MessagesSent, numberOfMessages);
            Assert.True(producer.ConfirmFrames >= 1);
            Assert.True(producer.IncomingFrames >= 1);
            Assert.True(producer.PublishCommandsSent >= 1);
            producer.Dispose();
        }

        private class Connection
        {
            public string name { get; set; }
            public Dictionary<string, string> client_properties { get; set; }
        }

        public static async Task<bool> IsConnectionOpen(string connectionName)
        {
            using var handler = new HttpClientHandler { Credentials = new NetworkCredential("guest", "guest"), };
            using var client = new HttpClient(handler);
            var isOpen = false;

            var result = await client.GetAsync("http://localhost:15672/api/connections");
            if (!result.IsSuccessStatusCode)
            {
                throw new XunitException(string.Format("HTTP GET failed: {0} {1}", result.StatusCode, result.ReasonPhrase));
            }

            var obj = JsonSerializer.Deserialize(result.Content.ReadAsStream(), typeof(IEnumerable<Connection>));
            if (obj != null)
            {
                var connections = obj as IEnumerable<Connection>;
                isOpen = connections.Any(x => x.client_properties["connection_name"].Contains(connectionName));
            }

            return isOpen;
        }

        public static async Task<int> HttpKillConnections(string connectionName)
        {
            using var handler = new HttpClientHandler { Credentials = new NetworkCredential("guest", "guest"), };
            using var client = new HttpClient(handler);

            var uri = new Uri("http://localhost:15672/api/connections");
            var result = await client.GetAsync("http://localhost:15672/api/connections");
            if (!result.IsSuccessStatusCode)
            {
                throw new XunitException($"HTTP GET failed: {result.StatusCode} {result.ReasonPhrase}");
            }

            var json = await result.Content.ReadAsStringAsync();
            var connections = JsonSerializer.Deserialize<IEnumerable<Connection>>(json);
            if (connections == null)
            {
                return 0;
            }

            // we kill _only_ producer and consumer connections
            // leave the locator up and running to delete the stream
            var iEnumerable = connections.Where(x => x.client_properties["connection_name"].Contains(connectionName));
            var enumerable = iEnumerable as Connection[] ?? iEnumerable.ToArray();
            var killed = 0;
            foreach (var conn in enumerable)
            {
                /*
                 * NOTE:
                 * this is the equivalent to this JS code:
                 * https://github.com/rabbitmq/rabbitmq-server/blob/master/deps/rabbitmq_management/priv/www/js/formatters.js#L710-L712
                 *
                 * function esc(str) {
                 *   return encodeURIComponent(str);
                 * }
                 *
                 * https://stackoverflow.com/a/4550600
                 */
                var s = Uri.EscapeDataString(conn.name);
                var r = await client.DeleteAsync($"http://localhost:15672/api/connections/{s}");
                if (!r.IsSuccessStatusCode)
                {
                    throw new XunitException($"HTTP DELETE failed: {result.StatusCode} {result.ReasonPhrase}");
                }

                killed += 1;
            }

            return killed;
        }

        public static void HttpPost(string jsonBody, string api)
        {
            using var handler = new HttpClientHandler { Credentials = new NetworkCredential("guest", "guest"), };
            using var client = new HttpClient(handler);
            HttpContent content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            var task = client.PostAsync($"http://localhost:15672/api/{api}", content);
            task.Wait();
            var result = task.Result;
            if (!result.IsSuccessStatusCode)
            {
                throw new XunitException(string.Format("HTTP POST failed: {0} {1}", result.StatusCode, result.ReasonPhrase));
            }
        }

        public static byte[] GetFileContent(string fileName)
        {
            var codeBaseUrl = new Uri(Assembly.GetExecutingAssembly().Location);
            var codeBasePath = Uri.UnescapeDataString(codeBaseUrl.AbsolutePath);
            var dirPath = Path.GetDirectoryName(codeBasePath);
            if (dirPath != null)
            {
                var filename = Path.Combine(dirPath, "Resources", fileName);
                var fileTask = File.ReadAllBytesAsync(filename);
                fileTask.Wait(TimeSpan.FromSeconds(1));
                return fileTask.Result;
            }

            return null;
        }
    }
}
