﻿using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using System.Data;
using System.Data.Common;
using Squared.Task.Data.Extensions;
using Squared.Task.Data.Mapper;
using System.Linq;

// Requires System.Data.SQLite
#if SQLITE
using System.Data.SQLite;
using Squared.Util;
namespace Squared.Task.Data {
    [TestFixture]
    public class MemoryDbTests {
        SQLiteConnection Connection;

        [SetUp]
        public void SetUp () {
            Connection = new SQLiteConnection("Data Source=:memory:");
            Connection.Open();
        }

        internal void DoQuery (string sql) {
            var cmd = Connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
            cmd.Dispose();
        }

        [TearDown]
        public void TearDown () {
            Connection.Dispose();
            Connection = null;
        }

        [Test]
        public void TestAsyncExecuteScalar () {
            var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT 1";
            var f = cmd.AsyncExecuteScalar();
            f.GetCompletionEvent().WaitOne();
            Assert.AreEqual(1, f.Result);

            cmd.Dispose();
        }

        [Test]
        public void TestAsyncExecuteNonQuery () {
            var cmd = Connection.CreateCommand();
            cmd.CommandText = "CREATE TEMPORARY TABLE Test (value int); INSERT INTO Test (value) VALUES (1)";
            IFuture f = cmd.AsyncExecuteNonQuery();
            f.GetCompletionEvent().WaitOne();

            cmd.CommandText = "SELECT value FROM Test LIMIT 1";
            f = cmd.AsyncExecuteScalar();
            f.GetCompletionEvent().WaitOne();
            Assert.AreEqual(1, f.Result);

            cmd.Dispose();
        }

        [Test]
        public void TestAsyncExecuteReader () {
            DoQuery("CREATE TEMPORARY TABLE Test (value int)");
            for (int i = 0; i < 100; i++)
                DoQuery(String.Format("INSERT INTO Test (value) VALUES ({0})", i));

            var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM Test";
            var f = cmd.AsyncExecuteReader();
            f.GetCompletionEvent().WaitOne();

            var reader = (DbDataReader)f.Result;
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(0, reader.GetInt32(0));
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(1, reader.GetInt32(0));

            reader.Dispose();
            cmd.Dispose();
        }

        [Test]
        public void TestConnectionWrapper () {
            DoQuery("CREATE TEMPORARY TABLE Test (value int)");
            for (int i = 0; i < 100; i++)
                DoQuery(String.Format("INSERT INTO Test (value) VALUES ({0})", i));

            using (var scheduler = new TaskScheduler())
            using (var qm = new ConnectionWrapper(scheduler, Connection)) {
                var q = qm.BuildQuery("SELECT COUNT(value) FROM Test WHERE value = ?");

                var f = q.ExecuteScalar(5);
                var result = scheduler.WaitFor(f);

                Assert.AreEqual(result, 1);

                q = qm.BuildQuery("SELECT @p0 - @p1");

                f = q.ExecuteScalar(2, 3);                
                result = scheduler.WaitFor(f);

                Assert.AreEqual(result, -1);

                f = q.ExecuteScalar(new NamedParam { N = "p0", V = 4 }, new NamedParam { N = "p1", V = 3 });
                result = scheduler.WaitFor(f);

                Assert.AreEqual(result, 1);

                f = q.ExecuteScalar(5, new NamedParam { N = "p1", V = 3 });
                result = scheduler.WaitFor(f);

                Assert.AreEqual(result, 2);

                q = qm.BuildQuery("SELECT @parm1 - @parm2");

                f = q.ExecuteScalar(new NamedParam { N = "parm1", V = 1 }, new NamedParam { N = "parm2", V = 2 });
                result = scheduler.WaitFor(f);

                Assert.AreEqual(result, -1);
            }
        }

        [Test]
        public void TestDbTaskIterator () {
            DoQuery("CREATE TEMPORARY TABLE Test (value int)");
            for (int i = 0; i < 100; i++)
                DoQuery(String.Format("INSERT INTO Test (value) VALUES ({0})", i));

            using (var scheduler = new TaskScheduler())
            using (var qm = new ConnectionWrapper(scheduler, Connection)) {
                var q = qm.BuildQuery("SELECT value FROM Test WHERE value = ?");

                using (var iterator = q.Execute(5)) {
                    scheduler.WaitFor(iterator.Fetch());

                    using (var e = iterator.CurrentItems) {
                        Assert.IsTrue(e.MoveNext());
                        Assert.AreEqual(e.Current.GetInt32(0), 5);
                    }
                }
            }
        }

        [Test]
        public void TestQueryPipelining () {
            DoQuery("CREATE TEMPORARY TABLE Test (value int)");
            for (int i = 0; i < 100; i++)
                DoQuery(String.Format("INSERT INTO Test (value) VALUES ({0})", i));

            using (var scheduler = new TaskScheduler())
            using (var qm = new ConnectionWrapper(scheduler, Connection)) {
                var q1 = qm.BuildQuery("SELECT value FROM test");
                var q2 = qm.BuildQuery("INSERT INTO test (value) VALUES (?)");

                var iterator = q1.Execute();
                var f1 = scheduler.Start(iterator.Fetch());
                var f2 = q2.ExecuteNonQuery(200);

                f1.RegisterOnComplete((f) => {
                    Assert.IsNull(f.Error);
                    Assert.AreEqual(f1, f);
                    Assert.AreEqual(true, f.Result);
                    Assert.IsTrue(f1.Completed);
                    Assert.IsFalse(f2.Completed);
                });

                f2.RegisterOnComplete((f) => {
                    Assert.IsNull(f.Error);
                    Assert.AreEqual(f2, f);
                    Assert.IsTrue(f1.Completed);
                    Assert.IsTrue(f2.Completed);
                });

                scheduler.WaitFor(f1);

                scheduler.WaitFor(scheduler.Start(new Sleep(1.0)));
                Assert.IsFalse(f2.Completed);

                iterator.Dispose();

                scheduler.WaitFor(f2);
            }
        }

        [Test]
        public void TestTransactionPipelining () {
            DoQuery("CREATE TEMPORARY TABLE Test (value int)");

            using (var scheduler = new TaskScheduler())
            using (var qm = new ConnectionWrapper(scheduler, Connection)) {
                var getNumValues = qm.BuildQuery("SELECT COUNT(value) FROM test");

                var addValue = qm.BuildQuery("INSERT INTO test (value) VALUES (?)");

                var t = qm.CreateTransaction();
                var fq = addValue.ExecuteNonQuery(1);
                var fr = t.Rollback();

                scheduler.WaitFor(Future.WaitForAll(t.Future, fq, fr));

                var fgnv = getNumValues.ExecuteScalar();
                long numValues = Convert.ToInt64(
                    scheduler.WaitFor(fgnv)
                );
                Assert.AreEqual(0, numValues);

                t = qm.CreateTransaction();
                fq = addValue.ExecuteNonQuery(1);
                var fc = t.Commit();

                scheduler.WaitFor(Future.WaitForAll(t.Future, fq, fc));

                fgnv = getNumValues.ExecuteScalar();
                numValues = Convert.ToInt64(
                    scheduler.WaitFor(fgnv)
                );
                Assert.AreEqual(1, numValues);
            }
        }

        IEnumerator<object> CrashyTransactionTask (ConnectionWrapper cw, Query addValue) {
            using (var trans = cw.CreateTransaction()) {
                yield return addValue.ExecuteNonQuery(1);
                yield return addValue.ExecuteNonQuery();
                yield return trans.Commit();
            }
        }

        [Test]
        public void TestTransactionAutoRollback () {
            DoQuery("CREATE TEMPORARY TABLE Test (value int)");

            using (var scheduler = new TaskScheduler())
            using (var qm = new ConnectionWrapper(scheduler, Connection)) {
                var getNumValues = qm.BuildQuery("SELECT COUNT(value) FROM test");

                var addValue = qm.BuildQuery("INSERT INTO test (value) VALUES (?)");

                var f = scheduler.Start(CrashyTransactionTask(qm, addValue));
                try {
                    scheduler.WaitFor(f);
                    Assert.Fail("Did not throw");
                } catch (FutureException fe) {
                    Exception inner = fe.InnerException;
                    Assert.IsInstanceOfType(typeof(InvalidOperationException), inner);
                }

                var fgnv = getNumValues.ExecuteScalar();
                long numValues = Convert.ToInt64(
                    scheduler.WaitFor(fgnv)
                );
                Assert.AreEqual(0, numValues);
            }
        }

        [Test]
        public void TestNestedTransactions () {
            DoQuery("CREATE TEMPORARY TABLE Test (value int)");

            using (var scheduler = new TaskScheduler())
            using (var qm = new ConnectionWrapper(scheduler, Connection)) {
                var getNumValues = qm.BuildQuery("SELECT COUNT(value) FROM test");

                var addValue = qm.BuildQuery("INSERT INTO test (value) VALUES (?)");

                var t = qm.CreateTransaction();
                var fq = addValue.ExecuteNonQuery(1);
                var t2 = qm.CreateTransaction();
                var fq2 = addValue.ExecuteNonQuery(2);
                var fr = t2.Rollback();
                var fc = t.Commit();

                scheduler.WaitFor(Future.WaitForAll(t.Future, fq, t2.Future, fq2, fr, fc));

                var fgnv = getNumValues.ExecuteScalar();
                long numValues = Convert.ToInt64(
                    scheduler.WaitFor(fgnv)
                );
                Assert.AreEqual(0, numValues);

                t = qm.CreateTransaction();
                fq = addValue.ExecuteNonQuery(1);
                t2 = qm.CreateTransaction();
                fq2 = addValue.ExecuteNonQuery(2);
                var fc2 = t2.Commit();
                fc = t.Commit();

                scheduler.WaitFor(Future.WaitForAll(t.Future, fq, t2.Future, fq2, fc2, fc));

                fgnv = getNumValues.ExecuteScalar();
                numValues = Convert.ToInt64(
                    scheduler.WaitFor(fgnv)
                );
                Assert.AreEqual(2, numValues);
            }
        }

        [Test]
        public void TestQueryParameters () {
            using (var scheduler = new TaskScheduler())
            using (var wrapper = new ConnectionWrapper(scheduler, Connection)) {
                scheduler.WaitFor(wrapper.ExecuteSQL("CREATE TEMPORARY TABLE Test (a INTEGER, b VARIANT)"));

                using (var q = wrapper.BuildQuery("INSERT INTO Test (a, b) VALUES (?, ?)")) {
                    q.Parameters[1].DbType = DbType.Object;
                    Assert.AreEqual(DbType.Object, q.Parameters[1].DbType);
                }
            }
        }
    }

    [TestFixture]
    public class DiskDbTests {
        SQLiteConnection Connection;

        [SetUp]
        public void SetUp () {
            string filename = System.IO.Path.GetTempFileName();
            Connection = new SQLiteConnection(String.Format("Data Source={0}", filename));
            Connection.Open();
        }

        internal void DoQuery (string sql) {
            var cmd = Connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
            cmd.Dispose();
        }

        [TearDown]
        public void TearDown () {
            Connection.Dispose();
            Connection = null;
        }

        [Test]
        public void TestCloneConnectionWrapper () {
            DoQuery("DROP TABLE IF EXISTS Test");
            DoQuery("CREATE TABLE Test (value int)");
            for (int i = 0; i < 10; i++)
                DoQuery(String.Format("INSERT INTO Test (value) VALUES ({0})", i));

            using (var scheduler = new TaskScheduler())
            using (var qm = new ConnectionWrapper(scheduler, Connection)) {
                var f = qm.Clone();
                using (var dupe = (ConnectionWrapper)scheduler.WaitFor(f)) {
                    var q = dupe.BuildQuery("SELECT COUNT(value) FROM Test WHERE value = ?");
                    f = q.ExecuteScalar(5);
                    var result = scheduler.WaitFor(f);
                    Assert.AreEqual(result, 1);
                }
            }
        }

        [Test]
        public void TestClonePipelining () {
            DoQuery("DROP TABLE IF EXISTS Test");
            DoQuery("CREATE TABLE Test (value int)");
            for (int i = 0; i < 10; i++)
                DoQuery(String.Format("INSERT INTO Test (value) VALUES ({0})", i));

            using (var scheduler = new TaskScheduler())
            using (var qm = new ConnectionWrapper(scheduler, Connection)) {
                var q = qm.BuildQuery("SELECT * FROM Test");
                var iter = q.Execute();
                var iterF = scheduler.Start(iter.Fetch());
                var f = qm.Clone();

                Assert.IsFalse(f.Completed);

                iter.Dispose();
                iterF.Dispose();
                scheduler.WaitFor(f);
                using (var dupe = (ConnectionWrapper)f.Result) {
                    q = dupe.BuildQuery("SELECT COUNT(value) FROM Test WHERE value = ?");
                    f = q.ExecuteScalar(5);
                    var result = scheduler.WaitFor(f);
                    Assert.AreEqual(result, 1);
                }
            }
        }

        [Test]
        public void TestDisposal () {
            DoQuery("DROP TABLE IF EXISTS Test");
            DoQuery("CREATE TABLE Test (value int)");
            for (int i = 0; i < 10; i++)
                DoQuery(String.Format("INSERT INTO Test (value) VALUES ({0})", i));

            TaskEnumerator<IDataRecord> iter;
            IFuture f;

            using (var scheduler = new TaskScheduler())
            using (var qm = new ConnectionWrapper(scheduler, Connection)) {
                var q = qm.BuildQuery("SELECT * FROM Test");
                var q2 = qm.BuildQuery("SELECT COUNT(*) FROM Test");
                iter = q.Execute();
                scheduler.Start(iter.Fetch());
                f = q2.ExecuteScalar();
            }

            iter.Dispose();

            try {
                int count = (int)f.Result;
                Assert.Fail("Future's result was not a ConnectionDisposedException");
            } catch (FutureException fe) {
                Assert.IsInstanceOfType(typeof(ConnectionDisposedException), fe.InnerException);
            }
        }
    }

    [TestFixture]
    public class MapperTests {
        public class ImplicitlyMappedClass {
            public long A {
                get;
                set;
            }
            public long B;
        }

        [Mapper(Explicit=true)]
        public class ExplicitlyMappedClass {
            [Column(0)]
            public long Foo {
                get;
                set;
            }
            [Column("B")]
            public long Bar;
        }

        public struct ImplicitlyMappedStruct {
            public long A {
                get;
                set;
            }
            public long B;
        }

        [Mapper(Explicit = true)]
        public struct ExplicitlyMappedStruct {
            [Column(0)]
            public long Foo {
                get;
                set;
            }
            [Column("B")]
            public long Bar;
        }

        SQLiteConnection Connection;
        TaskScheduler Scheduler;
        ConnectionWrapper Wrapper;

        [SetUp]
        public void SetUp () {
            Connection = new SQLiteConnection("Data Source=:memory:");
            Connection.Open();
            Scheduler = new TaskScheduler();
            Wrapper = new ConnectionWrapper(Scheduler, Connection);
        }

        [TearDown]
        public void TearDown () {
            Scheduler.WaitFor(Wrapper.Dispose());
            Scheduler.Dispose();
            Connection.Dispose();
            Connection = null;
        }

        [Test]
        public void TestImplicitMappingClass () {
            Scheduler.WaitFor(Wrapper.ExecuteSQL("CREATE TEMPORARY TABLE Test (a int, b int)"));

            int rowCount = 100;

            using (var q = Wrapper.BuildQuery("INSERT INTO TEST (a, b) VALUES (?, ?)"))
            for (int i = 0; i < rowCount; i++)
                Scheduler.WaitFor(q.ExecuteNonQuery(i, i * 2));

            using (var q = Wrapper.BuildQuery("SELECT a, b FROM Test"))
            using (var e = q.Execute<ImplicitlyMappedClass>()) {
                var items = (ImplicitlyMappedClass[])Scheduler.WaitFor(e.GetArray());

                for (int i = 0; i < rowCount; i++) {
                    Assert.AreEqual(i, items[i].A);
                    Assert.AreEqual(i * 2, items[i].B);
                }

                Assert.AreEqual(rowCount, items.Length);
            }
        }

        [Test]
        public void TestExplicitMappingClass () {
            Scheduler.WaitFor(Wrapper.ExecuteSQL("CREATE TEMPORARY TABLE Test (a int, b int)"));

            int rowCount = 100;

            using (var q = Wrapper.BuildQuery("INSERT INTO Test (a, b) VALUES (?, ?)"))
                for (int i = 0; i < rowCount; i++)
                    Scheduler.WaitFor(q.ExecuteNonQuery(i, i * 2));

            using (var q = Wrapper.BuildQuery("SELECT a, b FROM Test"))
            using (var e = q.Execute<ExplicitlyMappedClass>()) {
                var items = (ExplicitlyMappedClass[])Scheduler.WaitFor(e.GetArray());

                Assert.AreEqual(rowCount, items.Length);

                for (int i = 0; i < rowCount; i++) {
                    Assert.AreEqual(i, items[i].Foo);
                    Assert.AreEqual(i * 2, items[i].Bar);
                }
            }
        }
    }

    [TestFixture]
    public class PropertySerializerTests {
        public class ClassWithProperties {
            public long A {
                get;
                set;
            }
            public long B;
            public string C;
            public DateTime D {
                get;
                set;
            }
        }

        SQLiteConnection Connection;
        TaskScheduler Scheduler;
        ConnectionWrapper Wrapper;

        [SetUp]
        public void SetUp () {
            Connection = new SQLiteConnection("Data Source=:memory:");
            Connection.Open();
            Scheduler = new TaskScheduler();
            Wrapper = new ConnectionWrapper(Scheduler, Connection);
        }

        [TearDown]
        public void TearDown () {
            Scheduler.WaitFor(Wrapper.Dispose());
            Scheduler.Dispose();
            Connection.Dispose();
            Connection = null;
        }

        [Test]
        public void TestPropertySerializerSaveAndLoad () {
            Scheduler.WaitFor(Wrapper.ExecuteSQL("CREATE TEMPORARY TABLE Data (name TEXT, value VARIANT)"));

            var props = new ClassWithProperties();
            var serializer = new PropertySerializer(Wrapper, "Data");

            serializer.Bind(() => props.A);
            serializer.Bind(() => props.B);
            serializer.Bind(() => props.C);
            serializer.Bind(() => props.D);

            var timeA = DateTime.UtcNow;
            var timeB = timeA.AddDays(1);

            props.A = 5;
            props.B = 10;
            props.C = "Test";
            props.D = timeA;

            Scheduler.WaitFor(serializer.Save());

            Assert.AreEqual(
                4,
                Scheduler.WaitFor(Wrapper.ExecuteScalar<long>("SELECT COUNT(value) FROM Data"))
            );

            props.A = props.B = 0;
            props.C = "Baz";
            props.D = timeB;

            Scheduler.WaitFor(serializer.Load());

            Assert.AreEqual(
                5, props.A
            );
            Assert.AreEqual(
                10, props.B
            );
            Assert.AreEqual(
                "Test", props.C
            );
            Assert.AreEqual(
                timeA, props.D
            );
        }

        [Test]
        public void TestPropertySerializerLoadMissingValues () {
            Scheduler.WaitFor(Wrapper.ExecuteSQL("CREATE TEMPORARY TABLE Data (name TEXT, value VARIANT)"));

            var props = new ClassWithProperties();
            var serializer = new PropertySerializer(Wrapper, "Data");

            serializer.Bind(() => props.A);
            serializer.Bind(() => props.C);

            var timeA = DateTime.UtcNow;
            var timeB = timeA.AddDays(1);

            props.A = 5;
            props.C = "Test";

            Scheduler.WaitFor(serializer.Save());

            Assert.AreEqual(
                2,
                Scheduler.WaitFor(Wrapper.ExecuteScalar<long>("SELECT COUNT(value) FROM Data"))
            );

            serializer.Bind(() => props.B);
            serializer.Bind(() => props.D);

            props.A = props.B = 0;
            props.C = "Baz";
            props.D = timeB;

            Scheduler.WaitFor(serializer.Load());

            Assert.AreEqual(
                5, props.A
            );
            Assert.AreEqual(
                0, props.B
            );
            Assert.AreEqual(
                "Test", props.C
            );
            Assert.AreEqual(
                timeB, props.D
            );
        }
    }
}
#endif