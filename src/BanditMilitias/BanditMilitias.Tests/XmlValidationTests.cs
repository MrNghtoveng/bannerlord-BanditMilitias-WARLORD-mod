using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO;

namespace BanditMilitias.Tests
{
    [TestClass]
    public sealed class XmlValidationTests
    {
        private static readonly string ValidatorScriptPath = Path.Combine(TestSourceHelper.ProjectRoot, "tools", "Validate-ModuleDataXml.ps1");

        [TestMethod]
        public void ModuleData_Xml_Validator_Passes_Against_Game_Data()
        {
            Assert.IsTrue(File.Exists(ValidatorScriptPath), $"Validator script bulunamadi: {ValidatorScriptPath}");

            string gameRoot = TestSourceHelper.ResolveGameRoot();
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{ValidatorScriptPath}\" " +
                    $"-ProjectRoot \"{TestSourceHelper.ProjectRoot}\" -GameRoot \"{gameRoot}\" -CheckDistSync",
                WorkingDirectory = TestSourceHelper.ProjectRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            string stdout;
            string stderr;
            int exitCode;

            using (var process = Process.Start(psi))
            {
                Assert.IsNotNull(process);

                stdout = process!.StandardOutput.ReadToEnd();
                stderr = process.StandardError.ReadToEnd();

                if (!process.WaitForExit(180000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                    }

                    throw new TimeoutException("XML validator 180 saniye icinde tamamlanmadi.");
                }

                exitCode = process.ExitCode;
            }

            string combinedOutput = string.Join(
                Environment.NewLine,
                new[]
                {
                    "=== STDOUT ===",
                    stdout.Trim(),
                    "=== STDERR ===",
                    stderr.Trim(),
                });

            Assert.AreEqual(0, exitCode, $"XML validator basarisiz oldu.{Environment.NewLine}{combinedOutput}");
        }
    }
}
