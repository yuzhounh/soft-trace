Option Explicit
Dim fso, shell, projectRoot, command, argument
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
projectRoot = fso.GetParentFolderName(fso.GetParentFolderName(WScript.ScriptFullName))
command = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File """ & projectRoot & "\scripts\Apply-LocalPackage.ps1"" -Launch"
For Each argument In WScript.Arguments
    If argument = "--background" Then command = command & " -Background"
Next
shell.Run command, 0, False
