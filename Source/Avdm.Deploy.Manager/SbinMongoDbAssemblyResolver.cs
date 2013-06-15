﻿using System;
using System.Collections.Concurrent;
using System.Configuration;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Avdm.Deploy.Sbin;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Builders;
using MongoDB.Driver.GridFS;
using StructureMap;

namespace Avdm.Deploy.Manager
{
    /// <summary>
    /// Implements ISbinAssemblyResolver
    /// Loads requested types for the current version from MonogDB
    /// </summary>
    public class SbinMongoDbAssemblyResolver : ISbinAssemblyResolver
    {
        private string m_basePath;
        private string m_assemblyName;
        private MongoServer m_svr;
        private MongoDatabase m_db;
        private MongoGridFS m_grid;
        private readonly ConcurrentDictionary<string, Assembly> m_assemblies = new ConcurrentDictionary<string, Assembly>( StringComparer.InvariantCultureIgnoreCase );
        private readonly object m_syncAsmLoad = new object();
        private long m_version = -1;

        public string MainAssemblyName { get; private set; }

        public long CurrentVersion{ get { return m_version; } }

        public bool IsRunningInSbin { get { return true; } }

        public void Initialise( string basePath, long version, string exeName, string[] remainingArgs )
        {
            Console.WriteLine( "AssemblyResolver {0}, basePath={1}, v={2}, exe={3} args={4}", GetType().Assembly.GetName().Version, basePath, version, exeName, string.Join( ",", remainingArgs ?? new string[] { } ) );

            m_basePath = basePath;
            m_assemblyName = exeName;
            MainAssemblyName = m_assemblyName;
            m_version = version;

            var client = new MongoClient( ConfigurationManager.AppSettings["MongoDB.Server"] );
            m_svr = client.GetServer();
            m_db = m_svr.GetDatabase( "sbin" );

            m_grid = m_db.GridFS;

            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomainAssemblyResolve;
            Initialise();
        }

        private void Initialise()
        {
            ObjectFactory.Configure( x => x.For<ISbinAssemblyResolver>().Singleton().Use( () => this ) );
            m_assemblies[GetType().Name] = GetType().Assembly; //TODO test caching of this assembly
        }

        private Assembly CurrentDomainAssemblyResolve( object sender, ResolveEventArgs args )
        {
            var assemblyname = args.Name.Split( ',' )[0];

            return GetAssembly( assemblyname );
        }

        public Tuple<byte[], byte[]> GetAssemblyBytes( string assemblyname )
        {
            string gridFileName = FormatGridFileName( assemblyname );

            if( m_grid.Exists( Query.Matches( "filename", new BsonRegularExpression( new Regex( "^" + Regex.Escape( gridFileName + ".dll" ) + "$", RegexOptions.IgnoreCase ) ) ) ) )
            {
                gridFileName = gridFileName + ".dll";
            }
            else
            {
                if( m_grid.Exists( Query.Matches( "filename", new BsonRegularExpression( new Regex( "^" + Regex.Escape( gridFileName + ".exe" ) + "$", RegexOptions.IgnoreCase ) ) ) ) )
                {
                    gridFileName = gridFileName + ".exe";
                }
                else
                {
                    return null;
                }
            }

            byte[] asmBytes = ReadFile( gridFileName );
            byte[] pdbBytes = null;

            if( m_grid.Exists( Path.ChangeExtension( gridFileName, "pdb" ) ) )
            {
                pdbBytes = ReadFile( Path.ChangeExtension( gridFileName, "pdb" ) );
            }

            return new Tuple<byte[], byte[]>( asmBytes, pdbBytes );
        }

        public Assembly GetAssembly( string assemblyname )
        {
            Assembly asm;

            if( m_assemblies.TryGetValue( assemblyname, out asm ) )
            {
                return asm;
            }

            lock( m_syncAsmLoad )
            {
                var bytes = GetAssemblyBytes( assemblyname );

                byte[] asmBytes = bytes.Item1;
                byte[] pdbBytes = bytes.Item2;

                var assembly = Assembly.Load( asmBytes, pdbBytes );
                m_assemblies[assemblyname] = assembly;

                return assembly;
            }
        }

        /// <summary>
        /// Create a new AppDomain, setup sbin and return the requested type
        /// 
        /// In the new app domain the sbin AssemblyResolve event wont have been configured. So any
        /// attempt to load a type will only look on the disk for the assembly and thus fail.
        /// This method will setup sbin in the new AppDomain and then return the type that the user requested
        /// </summary>
        public Tuple<AppDomain, object> CreateAndUnwrapAppDomain( string domainName, AppDomainSetup setup, string assemblyName, string typeName )
        {
            var domain = AppDomain.CreateDomain( domainName, null, setup );

            var helper = (AppDomainCreationHelper)domain.CreateInstanceAndUnwrap(
                typeof(AppDomainCreationHelper).Assembly.FullName,
                typeof(AppDomainCreationHelper).FullName,
                false,
                BindingFlags.CreateInstance,
                null,
                new object[] {m_version},
                null,
                null );

            var obj = helper.Create( assemblyName, typeName );
            return new Tuple<AppDomain, object>( domain, obj );
        }

        private byte[] ReadFile( string fileName )
        {
            var found = m_grid.FindOne( Query.Matches( "filename", new BsonRegularExpression( new Regex( "^" + Regex.Escape( fileName ) + "$", RegexOptions.IgnoreCase ) ) ) );

            byte[] bytes;
            using( var g = found.OpenRead() )
            {
                bytes = new byte[g.Length];
                g.Read( bytes, 0, bytes.Length );
            }

            return bytes;
        }

        private string FormatGridFileName( string assemblyname )
        {
            string path = Path.Combine( m_basePath, assemblyname );
            return path;
        }
    }
}
