﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace RimDev.Automation.Sql
{
    public class LocalDb : IDisposable
    {
        public static class Versions
        {
            public const string V11 = "11.0";
            public const string V12 = "12.0";
            public const string V13 = "13.0";

            private static readonly Lazy<IReadOnlyList<string>> LazyInstalledVersions
                = new Lazy<IReadOnlyList<string>>(() =>
                {
                    return new[] {
                            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Microsoft SQL Server Local DB\Installed Versions\11.0", "ParentInstance", null)  == null ? null : V11,
                            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Microsoft SQL Server Local DB\Installed Versions\12.0", "ParentInstance", null)  == null ? null : V12,
                            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Microsoft SQL Server Local DB\Installed Versions\13.0", "ParentInstance", null)  == null ? null : V13,
                        }
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList()
                    .AsReadOnly();
                });

            public static readonly IReadOnlyList<string> All
                = new List<string> { V11, V12, V13 }.AsReadOnly();

            public static IReadOnlyList<string> InstalledVersions
            {
                get { return LazyInstalledVersions.Value; }
            }
        }

        public string ConnectionString { get; private set; }

        public string DatabaseName { get; private set; }

        public string OutputFolder { get; private set; }

        public string DatabaseMdfPath { get; private set; }

        public string DatabaseLogPath { get; private set; }

        public string Version { get; protected set; }

        public string InstanceName { get; private set; }

        public string Location { get; protected set; }

        public Func<string> DatabaseSuffixGenerator { get; protected set; } 

        public int? ConnectionTimeout { get; protected set; }

        public bool MultipleActiveResultsSets { get; protected set; }

        public LocalDb(
            string databaseName = null,
            string version = Versions.V11,
            string location = null,
            string databasePrefix = "localdb",
            Func<string> databaseSuffixGenerator = null,
            int? connectionTimeout = null,
            bool multipleActiveResultSets = false,
            bool tryInstallingInstanceIfNotExists = false,
            string instanceName = null)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                throw new PlatformNotSupportedException("LocalDb only works on Windows platform");

            Location = location;
            Version = version.Replace("v", ""); //Try to stay backwards compatible by removing "v" to get the real version number
            DatabaseSuffixGenerator = databaseSuffixGenerator ?? DateTime.Now.Ticks.ToString;
            ConnectionTimeout = connectionTimeout;
            MultipleActiveResultsSets = multipleActiveResultSets;
            DatabaseName = string.IsNullOrWhiteSpace(databaseName)
                ? string.Format("{0}_{1}", databasePrefix, DatabaseSuffixGenerator())
                : databaseName;
            InstanceName = instanceName?.ToLower() ?? string.Format("v{0}", Version); //sqllocaldb.exe always uses lower case

            if (tryInstallingInstanceIfNotExists && !InstallLocalDbInstanceIfNotExists())
                throw new ApplicationException($"Could not start instance {InstanceName} of localDb with version {Version}");

            CreateDatabase();
        }

        public IDbConnection OpenConnection()
        {
            var connection = new SqlConnection(ConnectionString);
            connection.Open();
            return connection;
        }

        private void CreateDatabase()
        {
            OutputFolder = string.IsNullOrWhiteSpace(Location)
                ? (Location = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location))
                : Location;

            var mdfFilename = string.Format("{0}.mdf", DatabaseName);
            DatabaseMdfPath = Path.Combine(OutputFolder, mdfFilename);
            DatabaseLogPath = Path.Combine(OutputFolder, string.Format("{0}_log.ldf", DatabaseName));

            // Create Data Directory If It Doesn't Already Exist.
            if (!Directory.Exists(OutputFolder))
            {
                Directory.CreateDirectory(OutputFolder);
            }

            // If the database does not already exist, create it.
            if (!IsAttached())
            {
                var connectionString = string.Format(@"Data Source=(LocalDB)\{0};Initial Catalog=master;Integrated Security=True", InstanceName);
                using (var connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    var cmd = connection.CreateCommand();
                    cmd.CommandText = string.Format("CREATE DATABASE {0} ON (NAME = N'{0}', FILENAME = '{1}')", DatabaseName, DatabaseMdfPath);
                    cmd.ExecuteNonQuery();
                }
            }

            // Open newly created, or old database.
            ConnectionString = string.Format(
                @"Data Source=(LocalDB)\{0};Initial Catalog={1};Integrated Security=True;{2}{3}",
                InstanceName,
                DatabaseName,
                ConnectionTimeout == null ? null : string.Format("Connection Timeout={0};", ConnectionTimeout),
                MultipleActiveResultsSets == true ? "MultipleActiveResultSets=true;" : null);
        }

        private bool InstallLocalDbInstanceIfNotExists()
        {
            var startInfo = new ProcessStartInfo
            {
                Arguments = "info",
                FileName = "sqllocaldb.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var standardOutput = new StringBuilder();
            var standardError = new StringBuilder();

            using (var process = Process.Start(startInfo))
            {
                // read chunk-wise while process is running.
                while (!process.HasExited)
                {
                    standardOutput.Append(process.StandardOutput.ReadToEnd());
                    standardError.Append(process.StandardError.ReadToEnd());
                }

                // make sure not to miss out on any remaindings.
                standardOutput.Append(process.StandardOutput.ReadToEnd());
                standardError.Append(process.StandardError.ReadToEnd());
            }
            Console.WriteLine(standardOutput.ToString());

            var error = standardError.ToString();
            if (!string.IsNullOrEmpty(error))
                throw new ApplicationException(error);

            var instances = standardOutput.ToString().Split(new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

            if (!instances.Any(i => i.Trim().Equals(InstanceName)))
            {
                standardOutput.Clear();
                standardError.Clear();

                startInfo.Arguments = string.Format("create \"{0}\" {1} -s", InstanceName, Version);

                using (var process = Process.Start(startInfo))
                {
                    // read chunk-wise while process is running.
                    while (!process.HasExited)
                    {
                        standardOutput.Append(process.StandardOutput.ReadToEnd());
                        standardError.Append(process.StandardError.ReadToEnd());
                    }

                    // make sure not to miss out on any remaindings.
                    standardOutput.Append(process.StandardOutput.ReadToEnd());
                    standardError.Append(process.StandardError.ReadToEnd());
                }
                Console.WriteLine(standardOutput.ToString());

                error = standardError.ToString();
                if (!string.IsNullOrEmpty(error))
                    throw new ApplicationException(error);

                //Check if output says that instance is started
                var expectedOutput = string.Format("LocalDB instance \"{0}\" started.", InstanceName);
                var actualOutput = standardOutput.ToString();
                if (!actualOutput.Contains(expectedOutput))
                    return false;
            }

            return true;
        }

        private void DetachDatabase()
        {
            try
            {
                var connectionString = string.Format(@"Data Source=(LocalDB)\{0};Initial Catalog=master;Integrated Security=True", InstanceName);
                using (var connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    var cmd = connection.CreateCommand();
                    cmd.CommandText = string.Format("ALTER DATABASE {0} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; exec sp_detach_db '{0}'", DatabaseName);
                    cmd.ExecuteNonQuery();
                }
            }
            catch { }
            finally
            {
                if (File.Exists(DatabaseMdfPath)) File.Delete(DatabaseMdfPath);
                if (File.Exists(DatabaseLogPath)) File.Delete(DatabaseLogPath);
            }
        }

        public bool IsAttached()
        {
            return IsAttached(DatabaseName, InstanceName);
        }

        public static bool IsAttached(string databaseName, string instanceName = "v" + Versions.V11)
        {
            using (var connection = new SqlConnection(string.Format(@"Data Source=(LocalDB)\{0};Initial Catalog=master;Integrated Security=True", instanceName)))
            {
                using (var command = new SqlCommand(string.Format("SELECT db_id('{0}')", databaseName), connection))
                {
                    connection.Open();
                    return command.ExecuteScalar() != DBNull.Value;
                }
            }
        }

        public void Dispose()
        {
            if (IsAttached())
            {
                DetachDatabase();
            }
        }
    }
}
