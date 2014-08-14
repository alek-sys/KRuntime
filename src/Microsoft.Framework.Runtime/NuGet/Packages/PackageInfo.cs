﻿using System;
using System.IO;

namespace NuGet
{
    public class PackageInfo
    {
        private readonly IFileSystem _repositoryRoot;
        private readonly string _versionDir;
        private IPackage _package;

        public PackageInfo(IFileSystem repositoryRoot, string packageId, SemanticVersion version, string configuration, string versionDir)
        {
            _repositoryRoot = repositoryRoot;
            Id = packageId;
            Version = version;
            Configuration = configuration;
            _versionDir = versionDir;
        }

        public string Id { get; private set; }

        public SemanticVersion Version { get; private set; }

        public string Configuration { get; private set; }

        public IPackage Package
        {
            get
            {
                if (_package == null)
                {
                    var nuspecPath = Path.Combine(_versionDir, Configuration, string.Format("{0}.nuspec", Id));
                    _package = new UnzippedPackage(_repositoryRoot, nuspecPath);
                }

                return _package;
            }
        }
    }
}