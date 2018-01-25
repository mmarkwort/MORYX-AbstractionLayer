﻿using System;
using System.Linq;
using Marvin.AbstractionLayer.Resources;
using Marvin.Container;
using Moq;
using NUnit.Framework;

namespace Marvin.Resources.Management.Tests
{
    [TestFixture]
    public class TypeControllerTests
    {
        private IResourceTypeController _typeController;

        [SetUp]
        public void Setup()
        {
            // Mock of the container
            var containerMock = new Mock<IContainer>();
            containerMock.Setup(c => c.GetRegisteredImplementations(It.IsAny<Type>()))
                .Returns(() => new [] {typeof(MyResource), typeof(DerivedResource), typeof(ReferenceResource), typeof(NonPublicResource)});

            _typeController = new ResourceTypeController
            {
                Container = containerMock.Object
            };
            _typeController.Start();
        }

        [TearDown]
        public void AfterTest()
        {
            _typeController.Dispose();
        }

        [Test]
        public void ReadAndWriteProperties()
        {
            // Arrange: Create instance
            var resource = new MyResource {Id = 1, Foo = 1337};

            // Act: Build Proxy
            var proxy = (IMyResource)_typeController.GetProxy(resource);
            var duplicate = (IDuplicateFoo)proxy;

            // Assert
            Assert.AreEqual(resource.Foo, proxy.Foo);
            Assert.AreEqual(resource.Foo, duplicate.Foo);
            proxy.Foo = 187;
            // duplicate.Foo = 10; ReadOnly but still uses the same property
            Assert.AreEqual(187, resource.Foo);
            Assert.AreEqual(187, duplicate.Foo);
        }

        [Test]
        public void UseBaseProxyForDerivedType()
        {
            // Arrange: Create instance
            var baseInstance = new MyResource {Id = 2};
            var instance = new DerivedResource {Id = 3};

            // Act: Build Proxy
            var baseProxy = (IMyResource) _typeController.GetProxy(baseInstance);
            var proxy = (IMyResource) _typeController.GetProxy(instance);

            // Assert: Make sure proxy is still the base type
            Assert.AreEqual(baseProxy.GetType(), proxy.GetType());
        }

        [Test]
        public void CallMethodOnProxy()
        {
            // Arrange: Create instance
            var instance = new MyResource {Id= 4, Foo = 10};

            // Act: Build proxy and call method
            var proxy = (IMyResource) _typeController.GetProxy(instance);
            var result = proxy.MultiplyFoo(3);
            proxy.MultiplyFoo(2, 10);

            // Assert: Check result and modified foo
            Assert.AreEqual(30, result);
            Assert.AreEqual(70, proxy.Foo);
        }

        [Test]
        public void CallMethodOnDerivedType()
        {
            // Arrange: Create instance
            var instance = new DerivedResource { Id = 5, Foo = 10 };

            // Act: Build proxy and call method
            var proxy = (IMyResource)_typeController.GetProxy(instance);
            var result = proxy.MultiplyFoo(3);

            // Assert: Check result and modified foo
            Assert.AreEqual(40, result);
            Assert.AreEqual(40, proxy.Foo);
        }

        [Test]
        public void ForwardEventsFromProxy()
        {
            // Arrange: Create instance and proxy
            var instance = new MyResource {Id = 6};
            var proxy = (IMyResource) _typeController.GetProxy(instance);

            // Act: Register listener and change foo
            object eventSender = null, eventSender2 = null;
            int eventValue = 0;
            var finallyEven = false;
            Assert.DoesNotThrow(() => instance.Foo = 10);
            EventHandler<int> eventHandler = (sender, foo) =>
            {
                eventSender = sender;
                eventValue = foo;
            };
            proxy.FooChanged += eventHandler;
            proxy.FooEven += (sender, b) => finallyEven = b;
            proxy.SomeEvent += (sender, args) => eventSender2 = sender;
            instance.Foo = 100;
            instance.RaiseEvent();
            proxy.FooChanged -= eventHandler;

            // Assert: Check if eventSender is not null and equals the proxy
            Assert.NotNull(eventSender);
            Assert.NotNull(eventSender2);
            Assert.AreNotEqual(0, eventValue);
            Assert.AreEqual(proxy, eventSender);
            Assert.AreEqual(100, eventValue);
            Assert.IsTrue(finallyEven);
        }

        [Test]
        public void AfterDisposeTheProxyIsDetached()
        {
            // Arrange: Create a proxy and register to an event
            var instance = new MyResource {Id = 7};
            var proxy = (IMyResource)_typeController.GetProxy(instance);
            var called = false;
            proxy.FooChanged += (sender, i) => called = true;
            instance.Foo = 10;
            Assert.IsTrue(called);

            // Act: Dispose the type controller and use the proxy again
            called = false;
            _typeController.Dispose();
            instance.Foo = 10;
            
            // Assert: Event was not raised and proxy can no longer be used
            Assert.IsFalse(called);
            Assert.Throws<ProxyDetachedException>(() => proxy.MultiplyFoo(2));
        }

        [Test]
        public void ReplaceWithProxy()
        {
            // Arrange: Create instance and reference
            var ref1 = new DerivedResource { Id = 9, Foo = 20 };
            var ref2 = new MyResource {Id = 10, Foo = 30};
            var nonPub = new NonPublicResource {Name = "NonPublic"};
            var instance = new ReferenceResource
            {
                Id = 8,
                Reference = ref1,
                NonPublic = nonPub,
                MoreReferences = new IMyResource[]{ ref2 },
            };

            // Act: Convert to proxy and access the reference
            var proxy = (IReferenceResource)_typeController.GetProxy(instance);
            var reference = proxy.Reference;
            var methodRef = proxy.GetReference();
            var references = proxy.MoreReferences.ToArray();
            var nonPubProxy = proxy.NonPublic;

            IMyResource eventArgs = null;
            proxy.ReferenceChanged += (sender, resource) => eventArgs = resource;

            // Act: Set resource property through proxy
            proxy.Reference = references[0];
            proxy.SetReference(reference);
            
            // Make sure all references where replaced with proxies
            Assert.AreNotEqual(ref1, reference);
            Assert.AreNotEqual(ref2, references[0]);
            Assert.AreNotEqual(nonPub, nonPubProxy);
            Assert.AreEqual(20, reference.Foo);
            Assert.AreEqual(reference, methodRef);
            Assert.AreEqual(30, references[0].Foo);
            Assert.NotNull(eventArgs);
            Assert.AreEqual(30, eventArgs.Foo);
            Assert.AreEqual("NonPublic", nonPubProxy.Name);
            // Assert modifications of the setters
            Assert.AreEqual(instance.Reference, ref2);
            Assert.AreEqual(instance.MoreReferences.Count(), 2);
            Assert.AreEqual(instance.MoreReferences.ElementAt(1), ref1);
        }

    }
}