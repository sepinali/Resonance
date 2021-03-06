﻿using Resonance.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Resonance.Tests.Consuming
{
    [Collection("EventingRepo")]
    public class FunctionalOrderingTests
    {
        private readonly IEventPublisher _publisher;
        private readonly IEventConsumer _consumer;

        public FunctionalOrderingTests(EventingRepoFactoryFixture fixture)
        {
            _publisher = new EventPublisher(fixture.RepoFactory, DateTimeProvider.Repository, TimeSpan.Zero, InvokeOptions.NoRetries);
            _consumer = new EventConsumer(fixture.RepoFactory, TimeSpan.Zero, InvokeOptions.NoRetries);
        }

        [Fact]
        public void RetryOnVisibilityTimeout()
        {
            // Arrange
            var topicName = "Ordered.VisibilityTimemout";
            var subName = topicName + "_Sub1";
            var topic = _publisher.AddOrUpdateTopic(new Topic { Name = topicName });
            var sub1 = _consumer.AddOrUpdateSubscription(new Subscription
            {
                Name = subName,
                Ordered = true,
                DeliveryDelay = 1,
                TimeToLive = 60,
                MaxDeliveries = 0,
                TopicSubscriptions = new List<TopicSubscription> { new TopicSubscription { TopicId = topic.Id.Value, Enabled = true } },
            });

            var publishedDateUtcBaseLine = DateTime.UtcNow.AddSeconds(-10); // Explicitly setting publicationdates to make sure none are the same!
            _publisher.Publish(topicName, payload: "1", functionalKey: "1", publicationDateUtc: publishedDateUtcBaseLine.AddSeconds(1));
            _publisher.Publish(topicName, payload: "2", functionalKey: "1", publicationDateUtc: publishedDateUtcBaseLine.AddSeconds(2));

            var visibilityTimeout = 1;
            var e = _consumer.ConsumeNext(subName, visibilityTimeout: visibilityTimeout).SingleOrDefault();
            Assert.Equal("1", e.Payload);
            e = _consumer.ConsumeNext(subName, visibilityTimeout: visibilityTimeout).SingleOrDefault();
            Assert.Null(e); // event(payload) 2 has same functional key and should not be delivered yet

            // Try several times to make sure we don't run into race conditions
            for (int attempt = 0; attempt < 20; attempt++)
            {
                // Now wait until p1 expires
                do
                {
                    Thread.Sleep(2); // Not too long
                    e = _consumer.ConsumeNext(subName, visibilityTimeout: visibilityTimeout).SingleOrDefault();
                } while (e == null);
                Assert.Equal("1", e.Payload); // p1 should be delivered once again!
            }

            // Now mark it consumed and get the next event
            _consumer.MarkConsumed(e.Id, e.DeliveryKey); // p1 should be gone, so p2 can be delivered
            e = _consumer.ConsumeNext(subName, visibilityTimeout: visibilityTimeout).SingleOrDefault();
            Assert.Equal("2", e.Payload); // p2 should now be delivered, since p1 has been consumed on the second attempt
        }

        [Fact]
        public void SerialDelivery()
        {
            // Arrange
            var topicName = "FunctionalOrderingTests.SerialDelivery";
            var subName = topicName + "_Sub1"; // Substring to prevent too long sub-names
            var topic = _publisher.AddOrUpdateTopic(new Topic { Name = topicName });
            var sub1 = _consumer.AddOrUpdateSubscription(new Subscription
            {
                Name = subName,
                Ordered = true,
                MaxDeliveries = 2,
                TopicSubscriptions = new List<TopicSubscription> { new TopicSubscription { TopicId = topic.Id.Value, Enabled = true } },
            });

            var publishedDateUtcBaseLine = DateTime.UtcNow.AddSeconds(-60); // Explicitly setting publicationdates to make sure none are the same!
            _publisher.Publish(topicName, payload: "1", functionalKey: "f1", publicationDateUtc: publishedDateUtcBaseLine.AddSeconds(1));
            _publisher.Publish(topicName, payload: "2", functionalKey: "f1", publicationDateUtc: publishedDateUtcBaseLine.AddSeconds(2));
            _publisher.Publish(topicName, payload: "3", functionalKey: "f2", publicationDateUtc: publishedDateUtcBaseLine.AddSeconds(3));
            _publisher.Publish(topicName, payload: "4", functionalKey: "f1", publicationDateUtc: publishedDateUtcBaseLine.AddSeconds(4));
            _publisher.Publish(topicName, payload: "5", functionalKey: "f2", publicationDateUtc: publishedDateUtcBaseLine.AddSeconds(5));

            var visibilityTimeout = 5;
            var p1 = _consumer.ConsumeNext(subName, visibilityTimeout: visibilityTimeout).SingleOrDefault(); // p1 stands for payload "1"
            var p3 = _consumer.ConsumeNext(subName, visibilityTimeout: visibilityTimeout).SingleOrDefault();
            var p2 = _consumer.ConsumeNext(subName, visibilityTimeout: visibilityTimeout).SingleOrDefault();
            Assert.Equal("1", p1.Payload);
            Assert.Equal("3", p3.Payload);
            Assert.Null(p2); // p(ayload) 2 has same functional key as p1 and should not be delivered yet

            _consumer.MarkConsumed(p1.Id, p1.DeliveryKey); // p1 should be gone, so p2 can be delivered
            p2 = _consumer.ConsumeNext(subName, visibilityTimeout: visibilityTimeout).SingleOrDefault();
            var p4 = _consumer.ConsumeNext(subName, visibilityTimeout: visibilityTimeout).SingleOrDefault();
            Assert.Equal("2", p2.Payload);
            Assert.Null(p4); // Again: same functional key as p2, so should not be delivered (and p3 is still locked)
            Thread.Sleep(TimeSpan.FromSeconds(visibilityTimeout + 1)); // Wait until visibilitytimeout of all items has expired

            p2 = _consumer.ConsumeNext(subName, visibilityTimeout: visibilityTimeout).SingleOrDefault();
            p3 = _consumer.ConsumeNext(subName, visibilityTimeout: visibilityTimeout).SingleOrDefault();
            p4 = _consumer.ConsumeNext(subName, visibilityTimeout: visibilityTimeout).SingleOrDefault();
            Assert.Equal("2", p2.Payload); // p2 should be redelivered, since it had expired
            Assert.Equal("3", p3.Payload); // p3 should be redelivered, since it had expired
            Assert.Null(p4); // p4 still has same functional key as p2, so should not be delivered

            _consumer.MarkFailed(p2.Id, p2.DeliveryKey, Reason.Other("test"));
            p4 = _consumer.ConsumeNext(subName, visibilityTimeout: visibilityTimeout).SingleOrDefault();
            Assert.Equal("4", p4.Payload);

            Thread.Sleep(TimeSpan.FromSeconds(visibilityTimeout + 1)); // Wait until visibilitytimeout of all items has expired
            p4 = _consumer.ConsumeNext(subName, visibilityTimeout: visibilityTimeout).SingleOrDefault();
            var p5 = _consumer.ConsumeNext(subName, visibilityTimeout: visibilityTimeout).SingleOrDefault();
            Assert.Equal("4", p4.Payload); // p4 should be redeliverd (it was only delivered once)
            Assert.Equal("5", p5.Payload); // p3 has reached maxDeliveries, so p5 should now be delivered
        }

        // SerialDelivery_WithPriority was removed for 0.8.1, since taking priority into account while also using ordered delivery simply makes no sense.
        // For now only the MsSql-queries still support it, but this may be removed in the future as well.

        [Fact]
        public void SerialDelivery_SamePublicationDate()
        {
            // Arrange
            var topicName = "SerialDelivery_SamePublicationDate";
            var subName = topicName + "_Sub1"; // Substring to prevent too long sub-names
            var topic = _publisher.AddOrUpdateTopic(new Topic { Name = topicName });
            var sub1 = _consumer.AddOrUpdateSubscription(new Subscription
            {
                Name = subName,
                Ordered = true,
                TopicSubscriptions = new List<TopicSubscription> { new TopicSubscription { TopicId = topic.Id.Value, Enabled = true } },
            });

            var publishedDateUtcBaseLine = DateTime.UtcNow.AddSeconds(-60); // Explicitly setting publicationdates to make sure none are the same!
            _publisher.Publish(topicName, payload: "1", functionalKey: "f1", publicationDateUtc: publishedDateUtcBaseLine.AddSeconds(1));
            _publisher.Publish(topicName, payload: "2", functionalKey: "f1", publicationDateUtc: publishedDateUtcBaseLine.AddSeconds(2));
            _publisher.Publish(topicName, payload: "3", functionalKey: "f1", publicationDateUtc: publishedDateUtcBaseLine.AddSeconds(2));
            _publisher.Publish(topicName, payload: "4", functionalKey: "f1", publicationDateUtc: publishedDateUtcBaseLine.AddSeconds(3));

            var visibilityTimeout = 5;
            var p1 = _consumer.ConsumeNext(subName, visibilityTimeout: visibilityTimeout).SingleOrDefault(); // p1 stands for payload "1"
            Assert.Equal("1", p1.Payload);
            _consumer.MarkConsumed(p1.Id, p1.DeliveryKey);
            var p2 = _consumer.ConsumeNext(subName, visibilityTimeout: visibilityTimeout).SingleOrDefault();
            Assert.Equal("2", p2.Payload);
            _consumer.MarkConsumed(p2.Id, p2.DeliveryKey);
            var p3 = _consumer.ConsumeNext(subName, visibilityTimeout: visibilityTimeout).SingleOrDefault();
            Assert.Equal("3", p3.Payload); // P3 should be delivered, publicationdate is same as p2, but same <> overtaken!
            _consumer.MarkConsumed(p3.Id, p3.DeliveryKey);
            var p4 = _consumer.ConsumeNext(subName, visibilityTimeout: visibilityTimeout).SingleOrDefault();
            Assert.Equal("4", p4.Payload); // P3 should be delivered, since 
        }
    }
}
