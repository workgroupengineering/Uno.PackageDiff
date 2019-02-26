﻿using Mono.Cecil;
using Mono.Options;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Uno.PackageDiff
{
    class Program
	{
		private static readonly PackageSource NuGetOrgSource = new PackageSource("https://api.nuget.org/v3/index.json");

		static async Task<int> Main(string[] args)
        {
			string sourceArgument = null;
			string targetArgument = null;
			string outputFile = null;
			string diffIgnoreFile = null;

			var p = new OptionSet() {
				{ "base=", s => sourceArgument = s },
				{ "other=", s => targetArgument = s },
				{ "outfile=", s => outputFile = s },
				{ "diffignore=", s => diffIgnoreFile = s },
			};

			p.Parse(args);

			var source = await GetPackage(sourceArgument);
			var target = await GetPackage(targetArgument);

			try
			{
				var sourcePlatforms = Directory.GetDirectories(Path.Combine(source.path, "lib"), "*.*", SearchOption.TopDirectoryOnly);
				var targetPlatforms = Directory.GetDirectories(Path.Combine(target.path, "lib"), "*.*", SearchOption.TopDirectoryOnly);
				var platformsFiles = from sourceAssemblyPath in sourcePlatforms
									 join targetAssemblyPath in targetPlatforms on Path.GetFileName(sourceAssemblyPath) equals Path.GetFileName(targetAssemblyPath)
									 select new
									 {
										 Platform = Path.GetFileName(sourceAssemblyPath),
										 Source = sourceAssemblyPath,
										 Target = targetAssemblyPath
									 };

				var ignoreSet = DiffIgnore.ParseDiffIgnore(diffIgnoreFile, source.nuspecReader.GetVersion().ToString());

				bool failures = false;
				using (var writer = new StreamWriter(outputFile))
				{
					writer.WriteLine($"Comparison report for {source.nuspecReader.GetId()} **{source.nuspecReader.GetVersion()}** with {target.nuspecReader.GetId()} **{target.nuspecReader.GetVersion()}**");

					foreach(var platform in platformsFiles)
					{
						writer.WriteLine($"# {platform.Platform} Platform");

						foreach (var sourceFile in Directory.GetFiles(platform.Source, "*.dll"))
						{
							var targetFile = Path.Combine(platform.Target, Path.GetFileName(sourceFile));

							Console.WriteLine($"Comparing {sourceFile} and {targetFile}");

							failures |= CompareAssemblies(writer, sourceFile, targetFile, ignoreSet);
						}
					}
				}

				Console.WriteLine($"Done comparing.");

				return failures ? 1 : 0;
			}
			finally
			{
				Directory.Delete(source.path, true);
				Directory.Delete(target.path, true);
			}
		}

		public static bool CompareAssemblies(StreamWriter writer, string sourceFile, string targetFile, IgnoreSet ignoreSet)
		{
			using(var source = ReadModule(sourceFile))
			using(var target = ReadModule(targetFile))
			{
				writer.WriteLine($"## {Path.GetFileName(sourceFile)}");
				var results = AssemblyComparer.CompareTypes(source, target);

				ReportMissingTypes(writer, results, ignoreSet);
				ReportMethods(writer, results, ignoreSet);
				ReportEvents(writer, results, ignoreSet);
				ReportFields(writer, results, ignoreSet);
				ReportProperties(writer, results, ignoreSet);

				return ReportAnalyzer.IsDiffFailed(results, ignoreSet);
			}
		}

		private static void ReportMissingTypes(StreamWriter writer, ComparisonResult results, IgnoreSet ignoreSet)
		{
			writer.WriteLine("### {0} missing types:", results.InvalidTypes.Length);
			foreach(var invalidType in results.InvalidTypes)
			{
				var strike = ignoreSet.Types
					.Select(t => t.FullName)
					.Contains(invalidType.ToSignature())
					? "~~" : "";

				writer.WriteLine($"\t* {strike}`{invalidType.ToSignature()}`{strike}");
			}
		}

		private static void ReportProperties(StreamWriter writer, ComparisonResult results, IgnoreSet ignoreSet)
		{
			var groupedProperties = from method in results.InvalidProperties
								group method by method.DeclaringType.FullName into types
								select types;

			writer.WriteLine("### {0} missing or changed properties in existing types:", results.InvalidProperties.Length);

			foreach(var updatedType in groupedProperties)
			{
				writer.WriteLine("- `{0}`", updatedType.Key);
				foreach(var property in updatedType)
				{
					var strike = ignoreSet.Properties
						.Select(t => t.FullName)
						.Contains(property.ToSignature())
						? "~~" : "";

					writer.WriteLine($"\t* {strike}``{property.ToSignature()}``{strike}");
				}
			}
		}

		private static void ReportFields(StreamWriter writer, ComparisonResult results, IgnoreSet ignoreSet)
		{
			var groupedFields = from method in results.InvalidFields
								group method by method.DeclaringType.FullName into types
								select types;

			writer.WriteLine("### {0} missing or changed fields in existing types:", results.InvalidFields.Length);

			foreach(var updatedType in groupedFields)
			{
				writer.WriteLine("- `{0}`", updatedType.Key);
				foreach(var field in updatedType)
				{
					var strike = ignoreSet.Fields
						.Select(t => t.FullName)
						.Contains(field.ToSignature())
						? "~~" : "";

					writer.WriteLine($"\t* {strike}``{field.ToSignature()}``{strike}");
				}
			}
		}

		private static void ReportMethods(StreamWriter writer, ComparisonResult results, IgnoreSet ignoreSet)
		{
			var groupedMethods = from method in results.InvalidMethods
								 group method by method.DeclaringType.FullName into types
								 select types;

			writer.WriteLine("### {0} missing or changed method in existing types:", results.InvalidMethods.Length);

			foreach(var updatedType in groupedMethods)
			{
				writer.WriteLine("- `{0}`", updatedType.Key);
				foreach(var method in updatedType)
				{
					var methodSignature = method.ToSignature();

					var strike = ignoreSet.Methods
						.Select(t => t.FullName)
						.Contains(methodSignature)
						? "~~" : "";

					writer.WriteLine($"\t* {strike}``{methodSignature}``{strike}");
				}
			}
		}

		private static void ReportEvents(StreamWriter writer, ComparisonResult results, IgnoreSet ignoreSet)
		{
			var groupedEvents = from method in results.InvalidEvents
								 group method by method.DeclaringType.FullName into types
								 select types;

			writer.WriteLine("### {0} missing or changed events in existing types:", results.InvalidEvents.Length);

			foreach(var updatedType in groupedEvents)
			{
				writer.WriteLine("- `{0}`", updatedType.Key);
				foreach(var evt in updatedType)
				{
					var strike = ignoreSet.Events
						.Select(t => t.FullName)
						.Contains(evt.ToString())
						? "~~" : "";

					writer.WriteLine($"\t* {strike}``{evt.ToSignature()}``{strike}");
				}
			}
		}

		private static AssemblyDefinition ReadModule(string path)
		{
			var resolver = new DefaultAssemblyResolver();

			return AssemblyDefinition.ReadAssembly(path, new ReaderParameters() { AssemblyResolver = resolver });
		}

		private static async Task<(string path, NuspecReader nuspecReader)> GetPackage(string packagePath)
		{
			if(!packagePath.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
			{
				var settings = Settings.LoadDefaultSettings(null);
				var repositoryProvider = new SourceRepositoryProvider(settings, Repository.Provider.GetCoreV3());

				var repository = repositoryProvider.CreateRepository(NuGetOrgSource, FeedType.HttpV3);

				var searchResource = repository.GetResource<PackageSearchResource>();

				var packages = (await searchResource.SearchAsync(packagePath, new SearchFilter(true, SearchFilterType.IsLatestVersion), skip: 0, take: 1000, log: new NullLogger(), cancellationToken: CancellationToken.None)).ToArray();

				if(packages.Any())
				{
					var latestStable = (await packages.First().GetVersionsAsync())
						.OrderByDescending(v => v.Version)
						.Where(v => !v.Version.IsPrerelease)
						.FirstOrDefault();

					if (latestStable != null)
					{
						var url = $"https://api.nuget.org/v3-flatcontainer/{packagePath}/{latestStable.Version}/{packagePath}.{latestStable.Version}.nupkg";

						Console.WriteLine($"Downloading {url}");
						var outFile = Path.GetTempFileName();
						await new WebClient().DownloadFileTaskAsync(new Uri(url), outFile);
						return UnpackArchive(outFile);
					}
					else
					{
						throw new InvalidOperationException($"Unable to find stable {packagePath} in {NuGetOrgSource.SourceUri}");
					}
				}
				else
				{
					throw new InvalidOperationException($"Unable to find {packagePath} in {NuGetOrgSource.SourceUri}");
				}
			}
			else
			{
				return UnpackArchive(packagePath);
			}
		}

		private static (string path, NuspecReader nuspecReader) UnpackArchive(string packagePath)
		{
			var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString().Trim('{', '}'));

			Directory.CreateDirectory(path);

			Console.WriteLine($"Extracting {packagePath} -> {path}");
			using (var file = File.OpenRead(packagePath))
			{
				using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
				{
					archive.ExtractToDirectory(path);
				}
			}

			if(Directory.GetFiles(path, "*.nuspec", SearchOption.AllDirectories).FirstOrDefault() is string nuspecFile)
			{
				return (path, new NuspecReader(nuspecFile));
			}
			else
			{
				throw new InvalidOperationException($"Unable to find nuspec file in {packagePath}");
			}
		}

    }
}
