﻿// The MIT License (MIT)
// 
// Copyright (c) 2015-2017 Rasmus Mikkelsen
// Copyright (c) 2015-2017 eBay Software Foundation
// https://github.com/eventflow/EventFlow
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of
// this software and associated documentation files (the "Software"), to deal in
// the Software without restriction, including without limitation the rights to
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
// the Software, and to permit persons to whom the Software is furnished to do so,
// subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
// FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
// IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
// CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventFlow.Core;
using EventFlow.EventArchives;
using EventFlow.EventArchives.Formatters;
using EventFlow.EventArchives.Formatters.GZippedJson;
using EventFlow.EventArchives.Persistance.Files;
using EventFlow.EventStores;
using EventFlow.EventStores.InMemory;
using EventFlow.Logs;
using EventFlow.TestHelpers;
using EventFlow.TestHelpers.Aggregates;
using EventFlow.TestHelpers.Aggregates.Events;
using FluentAssertions;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;

namespace EventFlow.Tests.UnitTests.EventArchives.Persistance.Files
{
    [Category(Categories.Unit)]
    public class FileEventArchivePersistanceTests : TestsFor<FileEventArchivePersistance>
    {
        private Mock<IFileSystem> _fileSystemMock;
        private Mock<IFileEventArchiveConfiguration> _fileEventArchiveConfigurationMock;

        [SetUp]
        public void SetUp()
        {
            var eventDefinitionService = new EventDefinitionService(Mock<ILog>());
            eventDefinitionService.Load(typeof(ThingyPingEvent));

            Inject<IEventArchiveStreamFormatter>(new GZippedJsonEventArchiveStreamFormatter());

            _fileSystemMock = InjectMock<IFileSystem>();
            _fileEventArchiveConfigurationMock = InjectMock<IFileEventArchiveConfiguration>();
        }

        [Test]
        public void EventsAreArchived()
        {
            // Arrange
            var tmpFileName = Path.GetTempFileName();
            _fileEventArchiveConfigurationMock
                .Setup(m => m.GetEventArchiveFile(It.IsAny<IIdentity>()))
                .Returns(tmpFileName);

            // TODO cleanup

            var committedDomainEvents = Many<ICommittedDomainEvent>(27);
            var committedDomainEventStream = new InMemoryCommittedDomainEventStream(committedDomainEvents, 3);

            IReadOnlyCollection<GZippedJsonEventArchiveStreamFormatter.JsonEvent> jsonEvents = null;
            EventArchiveDetails eventArchiveDetails = null;

            using (var anonymousPipeServerStream = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None))
            {
                _fileSystemMock
                    .Setup(m => m.CreateAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                    .Returns(Task.FromResult<Stream>(anonymousPipeServerStream));

                var pipeHandle = anonymousPipeServerStream.GetClientHandleAsString();
                var writeTask = Task.Run(async () => {
                    eventArchiveDetails = await Sut.ArchiveAsync(A<ThingyId>(),
                        committedDomainEventStream,
                        CancellationToken.None)
                        .ConfigureAwait(false);
                });

                var readTask = Task.Run(() => {
                    using (var readStream = new AnonymousPipeClientStream(pipeHandle))
                    using (var gZipStream = new GZipStream(readStream, CompressionMode.Decompress))
                    using (var streamReader = new StreamReader(gZipStream, Encoding.UTF8))
                    using (var jsonTextReader = new JsonTextReader(streamReader))
                    {
                        var ss = new Newtonsoft.Json.JsonSerializer();
                        jsonEvents = ss.Deserialize<IReadOnlyCollection<GZippedJsonEventArchiveStreamFormatter.JsonEvent>>(jsonTextReader);
                    }
                });

                Task.WaitAll(writeTask, readTask);

                anonymousPipeServerStream.DisposeLocalCopyOfClientHandle();
            }

            jsonEvents.Should().NotBeNull();
            jsonEvents.Should().HaveCount(committedDomainEvents.Count);
            eventArchiveDetails.Should().NotBeNull();
            eventArchiveDetails.Uri.Should().Be(new Uri(tmpFileName));
        }
    }
}