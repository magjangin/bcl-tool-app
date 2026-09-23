using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BclToolApp.Models;
using BclToolApp.Services;

namespace BclToolApp.ViewModels;

public partial class LogAnalyzerViewModel : ObservableObject
{
    private readonly CrashLogAnalyzerService _analyzerService = new();

    [ObservableProperty]
    private string _logInputText = string.Empty;

    [ObservableProperty]
    private string _gameManagedPath = string.Empty;

    [ObservableProperty]
    private CrashDiagnosisResult? _diagnosis;

    [ObservableProperty]
    private bool _hasDiagnosis;

    [RelayCommand]
    private void AnalyzeLog()
    {
        if (string.IsNullOrWhiteSpace(LogInputText))
        {
            Diagnosis = null;
            HasDiagnosis = false;
            return;
        }

        Diagnosis = _analyzerService.Analyze(LogInputText, GameManagedPath);
        HasDiagnosis = true;
    }

    [RelayCommand]
    private void LoadSampleBclCrash()
    {
        LogInputText =
            "[MelonLoader] [00:00:01.234] Initializing MelonLoader v0.6.1...\n" +
            "[MelonLoader] [00:00:01.450] Loading Core dependencies...\n" +
            "[MelonLoader] [00:00:02.100] [ERROR] System.MissingMethodException: Method not found: 'System.Collections.Generic.IEnumerable`1<!!0> System.Linq.Enumerable.Select(System.Collections.Generic.IEnumerable`1<!!0>,System.Func`2<!!0,!!1>)'\n" +
            "  at MelonLoader.Core.Initialize () [0x00021] in <d48d1e345>:0 \n" +
            "  at MelonLoader.Bootstrap.Run () [0x00045] in <d48d1e345>:0 \n" +
            "[MelonLoader] [00:00:02.120] [FATAL] Bootstrap failed due to missing BCL API.";

        AnalyzeLog();
    }

    [RelayCommand]
    private void LoadSampleGameStrippedCrash()
    {
        LogInputText =
            "[MelonLoader] [00:00:05.100] Invoking OnApplicationStart in CustomMod.dll...\n" +
            "[CustomMod] [00:00:05.200] Searching for PlayerController in Assembly-CSharp...\n" +
            "[MelonLoader] [00:00:05.320] [ERROR] System.TypeLoadException: Could not load type 'Assembly-CSharp.PlayerCharacterController' from assembly 'Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null'.\n" +
            "  at CustomMod.Core.HookPlayer () [0x00010] in <9a8b7c>:0 \n" +
            "  at CustomMod.Main.OnApplicationStart () [0x00004] in <9a8b7c>:0 \n" +
            "[MelonLoader] [00:00:05.350] Mod initialization aborted.";

        AnalyzeLog();
    }

    [RelayCommand]
    private void Clear()
    {
        LogInputText = string.Empty;
        Diagnosis = null;
        HasDiagnosis = false;
    }
}
