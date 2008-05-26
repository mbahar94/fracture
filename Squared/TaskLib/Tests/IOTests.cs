﻿using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using System.IO;
using System.Threading;
using System.Net.Sockets;
using System.Net;
using System.Linq;

namespace Squared.Task {
    public class IOTests {
        public MemoryStream Stream;

        [SetUp]
        public virtual void SetUp () {
            Stream = new MemoryStream();
        }

        public void RewindStream () {
            Stream.Seek(0, SeekOrigin.Begin);
        }

        public void WriteTestData (byte[] data) {
            Stream.Write(data, 0, data.Length);
        }

        public void WriteTestData (string data) {
            WriteTestData(Encoding.ASCII.GetBytes(data));
        }

        public byte[] GetTestData () {
            return Stream.GetBuffer();
        }

        public string GetTestDataString () {
            return Encoding.ASCII.GetString(Stream.GetBuffer(), 0, (int)Stream.Length);
        }
    }

    [TestFixture]
    public class AsyncStreamReaderTests : IOTests {
        AsyncTextReader Reader;

        [SetUp]
        public override void SetUp () {
            base.SetUp();
            var adapter = new StreamDataAdapter(this.Stream);
            Reader = new AsyncTextReader(adapter, Encoding.ASCII);
        }

        [Test]
        public void ReadLineTest () {
            WriteTestData("abcd\r\nefgh\nijkl");
            RewindStream();

            Future f = Reader.ReadLine();
            f.GetCompletionEvent().WaitOne();
            Assert.AreEqual("abcd", f.Result);

            f = Reader.ReadLine();
            f.GetCompletionEvent().WaitOne();
            Assert.AreEqual("efgh", f.Result);

            f = Reader.ReadLine();
            f.GetCompletionEvent().WaitOne();
            Assert.AreEqual("ijkl", f.Result);

            f = Reader.ReadLine();
            f.GetCompletionEvent().WaitOne();
            Assert.AreEqual(null, f.Result);
        }

        [Test]
        public void ReadLotsOfLinesTest () {
            int lineCount = 5000;
            var strings = new List<string>();
            var rng = new Random();
            for (int i = 0; i < lineCount; i++) {
                string text = new string((char)rng.Next(32, 127), rng.Next(1, 64));
                strings.Add(text);
                WriteTestData(text);
                WriteTestData("\r\n");
            }

            RewindStream();

            var readLines = new List<string>();
            for (int i = 0; i < lineCount; i++) {
                Future f = Reader.ReadLine();
                f.GetCompletionEvent().WaitOne();
                string line = f.Result as string;
                readLines.Add(line);
            }

            Assert.AreEqual(strings.ToArray(), readLines.ToArray());
        }

        [Test]
        public void ReadToEndTest () {
            WriteTestData("abcd\r\nefgh\0ijkl");
            RewindStream();

            Future f = Reader.ReadToEnd();
            f.GetCompletionEvent().WaitOne();
            Assert.AreEqual("abcd\r\nefgh\0ijkl", f.Result);

            f = Reader.ReadToEnd();
            f.GetCompletionEvent().WaitOne();
            Assert.AreEqual(null, f.Result);
        }

        [Test]
        public void MultiblockReadTest () {
            string testData = new string('a', AsyncTextReader.DefaultBufferSize * 4);

            WriteTestData(testData);
            RewindStream();

            Future f = Reader.ReadToEnd();
            f.GetCompletionEvent().WaitOne();
            Assert.AreEqual(testData, f.Result);
        }

        [Test]
        public void ReadBlockTest () {
            string testData = new string('a', 320);

            WriteTestData(testData);
            RewindStream();

            char[] buffer = new char[256];
            Future f = Reader.Read(buffer, 0, 256);
            f.GetCompletionEvent().WaitOne();
            Assert.AreEqual(256, f.Result);
            Assert.AreEqual(testData.Substring(0, 256), new string(buffer, 0, 256));

            f = Reader.Read(buffer, 0, 256);
            f.GetCompletionEvent().WaitOne();
            Assert.AreEqual(64, f.Result);
            Assert.AreEqual(testData.Substring(256, 64), new string(buffer, 0, 64));

            f = Reader.Read(buffer, 0, 256);
            f.GetCompletionEvent().WaitOne();
            Assert.AreEqual(0, f.Result);
        }

        [Test]
        public void ReadAndPeekTest () {
            string testData = "ab";

            WriteTestData(testData);
            RewindStream();

            Future f = Reader.Peek();
            f.GetCompletionEvent().WaitOne();
            Assert.AreEqual('a', f.Result);

            f = Reader.Read();
            f.GetCompletionEvent().WaitOne();
            Assert.AreEqual('a', f.Result);

            f = Reader.Peek();
            f.GetCompletionEvent().WaitOne();
            Assert.AreEqual('b', f.Result);

            f = Reader.Read();
            f.GetCompletionEvent().WaitOne();
            Assert.AreEqual('b', f.Result);

            f = Reader.Peek();
            f.GetCompletionEvent().WaitOne();
            Assert.AreEqual(null, f.Result);

            f = Reader.Read();
            f.GetCompletionEvent().WaitOne();
            Assert.AreEqual(null, f.Result);
        }

        [Test]
        public void ThrowsIfReadInvokedWhilePreviousReadIsPending () {
            WriteTestData(new byte[1024 * 1024]);
            RewindStream();

            char[] buf = new char[2048 * 1024];

            Future f = Reader.Read(buf, 0, buf.Length);
            try {
                f = Reader.Read(buf, 0, 16);
                Assert.Fail("Read did not raise an OperationPending exception");
            } catch (OperationPendingException) {
            }
        }
    }

    [TestFixture]
    public class AsyncStreamWriterTests : IOTests {
        AsyncTextWriter Writer;

        [SetUp]
        public override void SetUp () {
            base.SetUp();
            var adapter = new StreamDataAdapter(this.Stream);
            Writer = new AsyncTextWriter(adapter, Encoding.ASCII);
        }

        [Test]
        public void WriteLineTest () {
            Future f = Writer.WriteLine("test");
            f.GetCompletionEvent().WaitOne();

            f = Writer.WriteLine("foo");
            f.GetCompletionEvent().WaitOne();

            Assert.AreEqual("test\r\nfoo\r\n", GetTestDataString());
        }

        [Test]
        public void WriteStringTest () {
            Future f = Writer.Write("test");
            f.GetCompletionEvent().WaitOne();

            f = Writer.Write("foo");
            f.GetCompletionEvent().WaitOne();

            Assert.AreEqual("testfoo", GetTestDataString());
        }

        [Test]
        public void ThrowsIfWriteInvokedWhilePreviousWriteIsPending () {
            string buf = new string(' ', 2048 * 1024);
            Future f = Writer.Write(buf);
            try {
                f = Writer.Write("foo");
                Assert.Fail("Write did not raise an OperationPending exception");
            } catch (OperationPendingException) {
            }
        }
    }

    [TestFixture]
    public class SocketTests {
        TcpClient A, B;
        NetworkStream StreamA, StreamB;
        TcpListener Listener;

        [SetUp]
        public void SetUp () {
            Listener = new TcpListener(IPAddress.Any, 1235);
            Listener.Start();
            Future fA = Listener.AcceptIncomingConnection();
            Future fB = Network.ConnectTo("localhost", 1235);
            fA.GetCompletionEvent().WaitOne();
            A = fA.Result as TcpClient;
            fB.GetCompletionEvent().WaitOne();
            B = fB.Result as TcpClient;
            Listener.Stop();
            StreamA = A.GetStream();
            StreamB = B.GetStream();
        }

        [TearDown]
        public void TearDown () {
            A.Close();
            B.Close();
        }

        [Test]
        public void TestLotsOfBlockingWrites () {
            byte[] writeBuf = new byte[256];
            byte[] readBuf = new byte[256];

            for (int i = 0; i < 256; i++)
                writeBuf[i] = (byte)i;

            for (int i = 0; i < 100; i++)
                StreamB.Write(writeBuf, 0, writeBuf.Length);

            for (int i = 0; i < 100; i++) {
                StreamA.Read(readBuf, 0, readBuf.Length);
                Assert.AreEqual(readBuf, writeBuf);
            }
        }

        [Test]
        public void BeginReadInvokesCallbackWhenDataAvailable () {
            byte[] writeBuf = new byte[256];
            byte[] readBuf = new byte[256];

            for (int i = 0; i < 256; i++)
                writeBuf[i] = (byte)i;

            bool[] readCallbackInvoked = new bool[1];
            bool[] writeCallbackInvoked = new bool[1];

            AsyncCallback readCallback = (ar) => {
                int numBytes = StreamA.EndRead(ar);
                Assert.AreEqual(readBuf.Length, numBytes);
                readCallbackInvoked[0] = true;
            };

            AsyncCallback writeCallback = (ar) => {
                StreamB.EndWrite(ar);
                writeCallbackInvoked[0] = true;
            };

            StreamA.BeginRead(readBuf, 0, readBuf.Length, readCallback, null);

            StreamB.BeginWrite(writeBuf, 0, writeBuf.Length, writeCallback, null);

            while (!readCallbackInvoked[0] || !writeCallbackInvoked[0])
                Thread.Sleep(1);

            Assert.AreEqual(readBuf, writeBuf);

        }

        [Test]
        public void BeginReadInvokesCallbackWhenOtherSocketDisconnects () {
            byte[] readBuf = new byte[256];

            bool[] readCallbackInvoked = new bool[1];

            AsyncCallback readCallback = (ar) => {
                int numBytes = StreamA.EndRead(ar);
                Assert.AreEqual(0, numBytes);
                readCallbackInvoked[0] = true;
            };

            StreamA.BeginRead(readBuf, 0, readBuf.Length, readCallback, null);

            B.Close();

            Thread.Sleep(1000);

            Assert.IsTrue(readCallbackInvoked[0]);
        }

        [Test]
        public void BeginReadInvokesCallbackWhenSocketDisconnectsButEndReadRaises () {
            byte[] readBuf = new byte[256];

            bool[] readCallbackInvoked = new bool[1];

            AsyncCallback readCallback = (ar) => {
                try {
                    Thread.Sleep(1000);
                    int numBytes = StreamA.EndRead(ar);
                    Assert.Fail("ObjectDisposedException was not raised by EndRead");
                } catch (ObjectDisposedException) {
                }
                readCallbackInvoked[0] = true;
            };

            StreamA.BeginRead(readBuf, 0, readBuf.Length, readCallback, null);
            A.Close();

            Thread.Sleep(2000);

            Assert.IsTrue(readCallbackInvoked[0]);
        }

        [Test]
        public void FillingSendBufferCausesBeginWriteToBlockEvenIfSocketIsNonBlocking () {
            byte[] buf = new byte[102400];

            A.Client.Blocking = false;
            B.Client.Blocking = false;

            StreamA.Write(buf, 0, buf.Length);

            Future f = new Future();

            StreamA.BeginWrite(buf, 0, buf.Length, (ar) => {
                try {
                    StreamA.EndWrite(ar);
                    f.Complete();
                } catch (Exception ex) {
                    f.Fail(ex);
                }
            }, null);

            Thread.Sleep(3000);

            Assert.IsFalse(f.Completed);

            A.Close();
            B.Close();
            StreamA.Dispose();
            StreamB.Dispose();

            GC.Collect();

            Thread.Sleep(1000);

            Assert.IsTrue(f.Completed);
            Assert.IsTrue(f.Failed);
        }

        [Test]
        public void FillingSendBufferCausesWriteToBlock () {
            byte[] buf = new byte[102400];

            StreamA.Write(buf, 0, buf.Length);

            Future f = new Future();

            ThreadPool.QueueUserWorkItem((_) => {
                try {
                    StreamA.Write(buf, 0, buf.Length);
                    f.Complete();
                } catch (Exception ex) {
                    f.Fail(ex);
                }
            }, null);

            Thread.Sleep(3000);

            Assert.IsFalse(f.Completed);

            A.Close();
            B.Close();
            StreamA.Dispose();
            StreamB.Dispose();

            GC.Collect();

            Thread.Sleep(1000);

            Assert.IsTrue(f.Completed);
            Assert.IsTrue(f.Failed);
        }

        [Test]
        public void FillingSendBufferCausesWriteToThrowIfSocketIsNonBlocking () {
            byte[] buf = new byte[102400];

            A.Client.Blocking = false;
            B.Client.Blocking = false;

            StreamA.Write(buf, 0, buf.Length);

            Future f = new Future();

            ThreadPool.QueueUserWorkItem((_) => {
                try {
                    StreamA.Write(buf, 0, buf.Length);
                    f.Complete();
                } catch (Exception ex) {
                    f.Fail(ex);
                }
            }, null);

            Thread.Sleep(3000);

            Assert.IsTrue(f.Completed);
            Assert.IsTrue(f.Failed);
            try {
                var _ = f.Result;
            } catch (FutureException fe) {
                var ioe = (IOException)fe.InnerException;
                var se = (SocketException)ioe.InnerException;
                Assert.AreEqual(SocketError.WouldBlock, se.SocketErrorCode);
            }

            A.Close();
            B.Close();
            StreamA.Dispose();
            StreamB.Dispose();

            GC.Collect();

            Thread.Sleep(1000);
        }

        [Test]
        public void FillingSendBufferCausesPollToReturnFalseIfSocketIsNonBlocking () {
            byte[] buf = new byte[102400];

            A.Client.Blocking = false;
            B.Client.Blocking = false;

            Assert.IsTrue(A.Client.Poll(0, SelectMode.SelectWrite));
            StreamA.Write(buf, 0, buf.Length);

            Assert.IsFalse(A.Client.Poll(0, SelectMode.SelectWrite));
        }

        [Test]
        public void FillingSendBufferCauses0ByteWriteToThrowIfSocketIsNonBlocking () {
            byte[] buf = new byte[102400];

            A.Client.Blocking = false;
            B.Client.Blocking = false;

            StreamA.Write(buf, 0, buf.Length);

            Future f = new Future();

            ThreadPool.QueueUserWorkItem((_) => {
                try {
                    StreamA.Write(new byte[0], 0, 0);
                    f.Complete();
                } catch (Exception ex) {
                    f.Fail(ex);
                }
            }, null);

            Thread.Sleep(3000);

            Assert.IsTrue(f.Completed);
            Assert.IsTrue(f.Failed);
            try {
                var _ = f.Result;
            } catch (FutureException fe) {
                var ioe = (IOException)fe.InnerException;
                var se = (SocketException)ioe.InnerException;
                Assert.AreEqual(SocketError.WouldBlock, se.SocketErrorCode);
            }

            A.Close();
            B.Close();
            StreamA.Dispose();
            StreamB.Dispose();

            GC.Collect();

            Thread.Sleep(1000);
        }
    }
}
