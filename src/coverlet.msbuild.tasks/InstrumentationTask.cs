﻿// Copyright (c) Toni Solarin-Sodara
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.IO;
using Coverlet.Core;
using Coverlet.Core.Abstractions;
using Coverlet.Core.Helpers;
using Coverlet.Core.Symbols;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.Extensions.DependencyInjection;

using ILogger = Coverlet.Core.Abstractions.ILogger;

namespace Coverlet.MSbuild.Tasks
{
  public class InstrumentationTask : BaseTask
  {
    private readonly MSBuildLogger _logger;

    [Required]
    public string Path { get; set; }

    public string Include { get; set; }

    public string IncludeDirectory { get; set; }

    public string Exclude { get; set; }

    public string ExcludeByFile { get; set; }

    public string ExcludeByAttribute { get; set; }

    public bool IncludeTestAssembly { get; set; }

    public bool SingleHit { get; set; }

    public string MergeWith { get; set; }

    public bool UseSourceLink { get; set; }

    public bool SkipAutoProps { get; set; }

    public string DoesNotReturnAttribute { get; set; }

    public bool DeterministicReport { get; set; }

    public string ExcludeAssembliesWithoutSources { get; set; }

    [Output]
    public ITaskItem InstrumenterState { get; set; }

    public InstrumentationTask()
    {
      _logger = new MSBuildLogger(Log);
    }

    private static void AttachDebugger()
    {
      if (int.TryParse(Environment.GetEnvironmentVariable("COVERLET_MSBUILD_INSTRUMENTATIONTASK_DEBUG"), out int result) && result == 1)
      {
        Debugger.Launch();
        Debugger.Break();
      }
    }

    public override bool Execute()
    {
      AttachDebugger();

      IServiceCollection serviceCollection = new ServiceCollection();
      serviceCollection.AddTransient<IProcessExitHandler, ProcessExitHandler>();
      serviceCollection.AddTransient<IFileSystem, FileSystem>();
      serviceCollection.AddTransient<IAssemblyAdapter, AssemblyAdapter>();
      serviceCollection.AddTransient<ILogger, MSBuildLogger>(_ => _logger);
      serviceCollection.AddTransient<IRetryHelper, RetryHelper>();
      // We cache resolutions
      serviceCollection.AddSingleton<ISourceRootTranslator, SourceRootTranslator>(serviceProvider =>
          new SourceRootTranslator(Path, serviceProvider.GetRequiredService<ILogger>(), serviceProvider.GetRequiredService<IFileSystem>(), serviceProvider.GetRequiredService<IAssemblyAdapter>()));
      // We need to keep singleton/static semantics
      serviceCollection.AddSingleton<IInstrumentationHelper, InstrumentationHelper>();
      serviceCollection.AddSingleton<ICecilSymbolHelper, CecilSymbolHelper>();

      ServiceProvider = serviceCollection.BuildServiceProvider();

      try
      {
        IFileSystem fileSystem = ServiceProvider.GetService<IFileSystem>();

        var parameters = new CoverageParameters
        {
          IncludeFilters = Include?.Split(','),
          IncludeDirectories = IncludeDirectory?.Split(','),
          ExcludeFilters = Exclude?.Split(','),
          ExcludedSourceFiles = ExcludeByFile?.Split(','),
          ExcludeAttributes = ExcludeByAttribute?.Split(','),
          IncludeTestAssembly = IncludeTestAssembly,
          SingleHit = SingleHit,
          MergeWith = MergeWith,
          UseSourceLink = UseSourceLink,
          SkipAutoProps = SkipAutoProps,
          DeterministicReport = DeterministicReport,
          ExcludeAssembliesWithoutSources = ExcludeAssembliesWithoutSources,
          DoesNotReturnAttributes = DoesNotReturnAttribute?.Split(',')
        };

        var coverage = new Coverage(Path,
                                         parameters,
                                         _logger,
                                         ServiceProvider.GetService<IInstrumentationHelper>(),
                                         ServiceProvider.GetService<IFileSystem>(),
                                         ServiceProvider.GetService<ISourceRootTranslator>(),
                                         ServiceProvider.GetService<ICecilSymbolHelper>());

        CoveragePrepareResult prepareResult = coverage.PrepareModules();
        string randomPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
        InstrumenterState = new TaskItem(randomPath);
        using Stream instrumentedStateFile = fileSystem.NewFileStream(InstrumenterState.ItemSpec, FileMode.CreateNew, FileAccess.Write);
        using Stream serializedState = CoveragePrepareResult.Serialize(prepareResult);
        serializedState.CopyTo(instrumentedStateFile);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex);
        return false;
      }

      return true;
    }
  }
}
