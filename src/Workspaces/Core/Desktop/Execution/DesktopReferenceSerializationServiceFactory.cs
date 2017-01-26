﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Composition;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Execution
{
    /// <summary>
    /// this is desktop implementation of IReferenceSerializationService
    /// </summary>
    [ExportWorkspaceServiceFactory(typeof(IReferenceSerializationService), layer: ServiceLayer.Desktop), Shared]
    internal class ReferenceSerializationServiceFactory : IWorkspaceServiceFactory
    {
        private static readonly SerializationAnalyzerAssemblyLoader s_loader = new SerializationAnalyzerAssemblyLoader();

        public IWorkspaceService CreateService(HostWorkspaceServices workspaceServices)
        {
            return new Service(workspaceServices.GetService<ITemporaryStorageService>());
        }

        private sealed class Service : AbstractReferenceSerializationService
        {
            // cache for encoding. 
            // typical low number, high volumn data cache.
            private static readonly ConcurrentDictionary<Encoding, byte[]> s_encodingCache = new ConcurrentDictionary<Encoding, byte[]>(concurrencyLevel: 2, capacity: 5);

            public Service(ITemporaryStorageService service) : base(service)
            {
            }

            public override void WriteTo(Encoding encoding, ObjectWriter writer, CancellationToken cancellationToken)
            {
                if (encoding == null)
                {
                    base.WriteTo(encoding, writer, cancellationToken);
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                writer.WriteByte(EncodingSerialization);

                byte[] value;
                if (!s_encodingCache.TryGetValue(encoding, out value))
                {
                    // we don't have cache, cache it
                    var formatter = new BinaryFormatter();
                    using (var stream = SerializableBytes.CreateWritableStream())
                    {
                        // unfortunately, this is only way to properly clone encoding
                        formatter.Serialize(stream, encoding);
                        value = stream.ToArray();

                        // add if not already exist. otherwise, noop
                        s_encodingCache.TryAdd(encoding, value);
                    }
                }

                // write data out
                writer.WriteArray(value);
            }

            public override Encoding ReadEncodingFrom(ObjectReader reader, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var serialized = reader.ReadByte();
                if (serialized == EncodingSerialization)
                {
                    var array = (byte[])reader.ReadArray();
                    var formatter = new BinaryFormatter();

                    return (Encoding)formatter.Deserialize(new MemoryStream(array));
                }

                return ReadEncodingFrom(serialized, reader, cancellationToken);
            }

            protected override string GetAnalyzerAssemblyPath(AnalyzerFileReference reference)
            {
                // TODO: find out a way to get analyzer assembly location
                //       without actually loading analyzer in memory
                var assembly = reference.GetAssembly();
                return assembly?.Location;
            }

            protected override AnalyzerReference GetAnalyzerReference(string displayPath, string assemblyPath)
            {
                // desktop implementation doesn't do shadow copying and doesn't guarantee snapshot
                s_loader.AddPath(displayPath, assemblyPath);

                return new AnalyzerFileReference(assemblyPath, s_loader);
            }
        }
    }
}
