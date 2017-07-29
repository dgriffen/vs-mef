// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.VisualStudio.Composition.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Composition;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.Composition.AppDomainTests;
    using Microsoft.VisualStudio.Composition.AppDomainTests2;
    using Xunit;
    using Xunit.Abstractions;

    public abstract class CacheAndReloadTests
    {
        private readonly ITestOutputHelper logger;
        private ICompositionCacheManager cacheManager;

        protected CacheAndReloadTests(ITestOutputHelper logger, ICompositionCacheManager cacheManager)
        {
            Requires.NotNull(cacheManager, nameof(cacheManager));
            this.logger = logger;
            this.cacheManager = cacheManager;
        }

        [SkippableTheory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task CacheAndReload(bool stabilizeCatalog)
        {
            var catalog = TestUtilities.EmptyCatalog.AddParts(new[] { TestUtilities.V2Discovery.CreatePart(typeof(SomeExport)) });
            if (stabilizeCatalog)
            {
#if NET452
                var cachedCatalog = new CachedCatalog();
                catalog = cachedCatalog.Stabilize(catalog);
                GC.Collect(); // Shake out any collectible dynamic assembly bugs
#else
                throw new SkipException("Not applicable on .NET Core");
#endif
            }

            var configuration = CompositionConfiguration.Create(catalog);
            var ms = new MemoryStream();
            await this.cacheManager.SaveAsync(configuration, ms);
            configuration = null;

            ms.Position = 0;
            var exportProviderFactory = await this.cacheManager.LoadExportProviderFactoryAsync(ms, TestUtilities.Resolver);
            var container = exportProviderFactory.CreateExportProvider();
            SomeExport export = container.GetExportedValue<SomeExport>();
            Assert.NotNull(export);
        }

#if NET452
        [Fact(Skip = "Hack. Delete this.")]
        public async Task StabilizeVSCatalog()
        {
            using (var catalogReader = File.OpenRead(@"C:\Users\andarno\AppData\Local\microsoft\visualstudio\15.0_977a95f0\ComponentModelCache\Microsoft.VisualStudio.Default.catalogs"))
            {
                var binaryReader = new BinaryReader(catalogReader);
                long version = binaryReader.ReadInt64();
                int catalogCount = binaryReader.ReadInt32();
                var catalogs = new ComposableCatalog[catalogCount];
                var cacheSystem = new CachedCatalog();
                for (int i = 0; i < catalogCount; i++)
                {
                    catalogs[i] = await cacheSystem.LoadAsync(catalogReader, Resolver.DefaultInstance);
                }

                var unifiedCatalog = ComposableCatalog.Create(Resolver.DefaultInstance)
                    .AddCatalogs(catalogs);
                string assemblyPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".dll");
                var cachedCatalog = cacheSystem.Stabilize(unifiedCatalog, assemblyPath);
                this.logger.WriteLine(assemblyPath);
            }
        }
#endif

        [Export]
        public class SomeExport { }
    }
}
