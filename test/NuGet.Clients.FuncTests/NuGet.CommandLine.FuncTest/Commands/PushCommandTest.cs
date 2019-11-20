// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using FluentAssertions;
using NuGet.CommandLine.Test;
using NuGet.Commands;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Test.Utility;
using Test.Utility;
using Xunit;

namespace NuGet.CommandLine.FuncTest.Commands
{
    public class PushCommandTest
    {
        private const string MESSAGE_EXISTING_PACKAGE = "already exists at feed"; //Derived from resx: AddPackage_PackageAlreadyExists
        private const string MESSAGE_RESPONSE_NO_SUCCESS = "Response status code does not indicate success";
        private const string MESSAGE_PACKAGE_PUSHED = "Your package was pushed.";
        private const string TEST_PACKAGE_SHOULD_NOT_PUSH = "The package should not have been pushed";
        private const string TEST_PACKAGE_SHOULD_PUSH = "The package should have been pushed";
        private const string ADVERTISE_SKIPDUPLICATE_OPTION = "To skip already published packages, use the option -SkipDuplicate"; //PushCommandSkipDuplicateAdvertiseNuGetExe
        private const string WITHOUT_FILENAME_MESSAGE_FILE_DOES_NOT_EXIST = "File does not exist";
        private const string MESSAGE_FILE_DOES_NOT_EXIST = WITHOUT_FILENAME_MESSAGE_FILE_DOES_NOT_EXIST + " ({0})";

        /// <summary>
        /// 100 seconds is significant because that is the default timeout on <see cref="HttpClient"/>.
        /// Related to https://github.com/NuGet/Home/issues/2785.
        /// </summary>
        [Fact]
        public void PushCommand_AllowsTimeoutToBeSpecifiedHigherThan100Seconds()
        {
            // Arrange
            using (var packageDirectory = TestDirectory.Create())
            {
                var nuget = Util.GetNuGetExePath();
                var sourcePath = Util.CreateTestPackage("PackageA", "1.1.0", packageDirectory);
                var outputPath = Path.Combine(packageDirectory, "pushed.nupkg");

                using (var server = new MockServer())
                {
                    server.Put.Add("/push", r =>
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(101));

                        byte[] buffer = MockServer.GetPushedPackage(r);
                        using (var outputStream = new FileStream(outputPath, FileMode.Create))
                        {
                            outputStream.Write(buffer, 0, buffer.Length);
                        }

                        return HttpStatusCode.Created;
                    });

                    server.Start();

                    // Act
                    var result = CommandRunner.Run(
                        nuget,
                        packageDirectory,
                        $"push {sourcePath} -Source {server.Uri}push -Timeout 110",
                        waitForExit: true,
                        timeOutInMilliseconds: 120 * 1000); // 120 seconds

                    // Assert
                    server.Stop();
                    Assert.True(0 == result.Item1, $"{result.Item2} {result.Item3}");
                    Assert.Contains(MESSAGE_PACKAGE_PUSHED, result.Item2);
                    Assert.True(File.Exists(outputPath), TEST_PACKAGE_SHOULD_PUSH);
                    Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(outputPath));
                }
            }
        }

        [Fact]
        public void PushCommand_AllowsTimeoutToBeSpecifiedLowerThan100Seconds()
        {
            // Arrange
            using (var packageDirectory = TestDirectory.Create())
            {
                var nuget = Util.GetNuGetExePath();
                var sourcePath = Util.CreateTestPackage("PackageA", "1.1.0", packageDirectory);
                var outputPath = Path.Combine(packageDirectory, "pushed.nupkg");

                using (var server = new MockServer())
                {
                    server.Put.Add("/push", r =>
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(5));

                        byte[] buffer = MockServer.GetPushedPackage(r);
                        using (var outputStream = new FileStream(outputPath, FileMode.Create))
                        {
                            outputStream.Write(buffer, 0, buffer.Length);
                        }

                        return HttpStatusCode.Created;
                    });

                    server.Start();

                    // Act
                    var result = CommandRunner.Run(
                        nuget,
                        packageDirectory,
                        $"push {sourcePath} -Source {server.Uri}push -Timeout 1",
                        waitForExit: true,
                        timeOutInMilliseconds: 20 * 1000); // 20 seconds

                    // Assert
                    server.Stop();
                    Assert.True(1 == result.Item1, $"{result.Item2} {result.Item3}");
                    Assert.DoesNotContain(MESSAGE_PACKAGE_PUSHED, result.Item2);
                    Assert.False(File.Exists(outputPath), TEST_PACKAGE_SHOULD_NOT_PUSH);
                }
            }
        }

        [Fact]
        public void PushCommand_Server_SkipDuplicate_NotSpecified_PushHalts()
        {
            // Arrange
            using (var packageDirectory = TestDirectory.Create())
            {
                var nuget = Util.GetNuGetExePath();
                var sourcePath = Util.CreateTestPackage("PackageA", "1.1.0", packageDirectory);
                var outputPath = Path.Combine(packageDirectory, "pushed.nupkg");

                var sourcePath2 = Util.CreateTestPackage("PackageB", "1.1.0", packageDirectory);
                var outputPath2 = Path.Combine(packageDirectory, "pushed2.nupkg");

                using (var server = new MockServer())
                {
                    SetupMockServerForSkipDuplicate(server,
                                                      FuncOutputPath_SwitchesOnThirdPush(outputPath, outputPath2),
                                                      FuncStatusDuplicate_OccursOnSecondPush());

                    server.Start();


                    // Act
                    var result = CommandRunner.Run(
                        nuget,
                        packageDirectory,
                        $"push {sourcePath} -Source {server.Uri}push -Timeout 110",
                        waitForExit: true,
                        timeOutInMilliseconds: 120 * 1000); // 120 seconds

                    //Run again so that it will be a duplicate push.
                    var result2 = CommandRunner.Run(
                        nuget,
                        packageDirectory,
                        $"push {sourcePath} -Source {server.Uri}push -Timeout 110",
                        waitForExit: true,
                        timeOutInMilliseconds: 120 * 1000); // 120 seconds

                    var result3 = CommandRunner.Run(
                       nuget,
                       packageDirectory,
                       $"push {sourcePath2} -Source {server.Uri}push -Timeout 110",
                       waitForExit: true,
                       timeOutInMilliseconds: 120 * 1000); // 120 seconds

                    // Assert
                    server.Stop();
                    Assert.True(0 == result.Item1, $"{result.Item2} {result.Item3}");
                    Assert.Contains(MESSAGE_PACKAGE_PUSHED, result.Item2);
                    Assert.True(File.Exists(outputPath), TEST_PACKAGE_SHOULD_PUSH);
                    Assert.DoesNotContain(MESSAGE_RESPONSE_NO_SUCCESS, result.AllOutput);
                    Assert.DoesNotContain(MESSAGE_EXISTING_PACKAGE, result.AllOutput);
                    Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(outputPath));

                    // Second run of command is the duplicate.
                    Assert.False(0 == result2.Item1, result2.AllOutput);
                    Assert.Contains(MESSAGE_RESPONSE_NO_SUCCESS, result2.AllOutput);
                    Assert.DoesNotContain(MESSAGE_EXISTING_PACKAGE, result2.AllOutput);
                    Assert.Contains(ADVERTISE_SKIPDUPLICATE_OPTION, result2.AllOutput);
                    Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(outputPath));
                }
            }
        }

        [Fact]
        public void PushCommand_Server_SkipDuplicate_IsSpecified_PushProceeds()
        {
            // Arrange
            using (var packageDirectory = TestDirectory.Create())
            {
                var nuget = Util.GetNuGetExePath();
                var sourcePath = Util.CreateTestPackage("PackageA", "1.1.0", packageDirectory);
                var outputPath = Path.Combine(packageDirectory, "pushed.nupkg");

                var sourcePath2 = Util.CreateTestPackage("PackageB", "1.1.0", packageDirectory);
                var outputPath2 = Path.Combine(packageDirectory, "pushed2.nupkg");

                using (var server = new MockServer())
                {
                    SetupMockServerForSkipDuplicate(server,
                                                      FuncOutputPath_SwitchesOnThirdPush(outputPath, outputPath2),
                                                      FuncStatusDuplicate_OccursOnSecondPush());

                    server.Start();

                    // Act
                    var result = CommandRunner.Run(
                        nuget,
                        packageDirectory,
                        $"push {sourcePath} -Source {server.Uri}push -Timeout 110 -SkipDuplicate",
                        waitForExit: true,
                        timeOutInMilliseconds: 120 * 1000); // 120 seconds

                    //Run again so that it will be a duplicate push but use the option to skip duplicate packages.
                    var result2 = CommandRunner.Run(
                        nuget,
                        packageDirectory,
                        $"push {sourcePath} -Source {server.Uri}push -Timeout 110 -SkipDuplicate",
                        waitForExit: true,
                        timeOutInMilliseconds: 120 * 1000); // 120 seconds

                    //Third run with a different package.
                    var result3 = CommandRunner.Run(
                        nuget,
                        packageDirectory,
                        $"push {sourcePath2} -Source {server.Uri}push -Timeout 110 -SkipDuplicate",
                        waitForExit: true,
                        timeOutInMilliseconds: 120 * 1000); // 120 seconds

                    // Assert
                    server.Stop();
                    Assert.True(0 == result.Item1, $"{result.Item2} {result.Item3}");
                    Assert.Contains(MESSAGE_PACKAGE_PUSHED, result.AllOutput);
                    Assert.True(File.Exists(outputPath), TEST_PACKAGE_SHOULD_PUSH);
                    Assert.DoesNotContain(MESSAGE_RESPONSE_NO_SUCCESS, result.AllOutput);
                    Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(outputPath));

                    // Second run of command is the duplicate.
                    Assert.True(0 == result2.Item1, result2.AllOutput);
                    Assert.DoesNotContain(MESSAGE_PACKAGE_PUSHED, result2.AllOutput);
                    Assert.Contains(MESSAGE_EXISTING_PACKAGE, result2.AllOutput);
                    Assert.DoesNotContain(MESSAGE_RESPONSE_NO_SUCCESS, result2.AllOutput);

                    // Third run after a duplicate should be successful with the SkipDuplicate flag.
                    Assert.True(0 == result3.Item1, $"{result3.Item2} {result3.Item3}");
                    Assert.Contains(MESSAGE_PACKAGE_PUSHED, result3.AllOutput);
                    Assert.True(File.Exists(outputPath2), TEST_PACKAGE_SHOULD_PUSH);

                    Assert.Equal(File.ReadAllBytes(sourcePath2), File.ReadAllBytes(outputPath2));
                }
            }
        }

        /// <summary>
        /// When pushing a snupkg filename that doesn't exist, show a File Not Found error.
        /// </summary>
        [Fact]
        public void PushCommand_Server_Snupkg_FilenameDoesNotExist_FileNotFoundError()
        {
            // Arrange
            using (var packageDirectory = TestDirectory.Create())
            {
                var nuget = Util.GetNuGetExePath();
                string snupkgToPush = "nonExistingPackage.snupkg";

                using (var server = CreateAndStartMockV3Server(packageDirectory, out string sourceName))
                {
                    // Act
                    var result = CommandRunner.Run(
                        nuget,
                        packageDirectory,
                        $"push {snupkgToPush} -Source {sourceName} -Timeout 110",
                        waitForExit: true,
                        timeOutInMilliseconds: 120000); // 120 seconds

                    // Assert

                    string expectedFileNotFoundErrorMessage = string.Format(MESSAGE_FILE_DOES_NOT_EXIST, snupkgToPush);

                    Assert.False(result.Success, "File did not exist and should fail.");
                    Assert.DoesNotContain(MESSAGE_PACKAGE_PUSHED, result.Output);
                    Assert.Contains(expectedFileNotFoundErrorMessage, result.Errors);
                }
            }
        }

        /// <summary>
        /// When pushing a snupkg wildcard where no matching files exist, show a File Not Found error. 
        /// </summary>
        [Fact]
        public void PushCommand_Server_Snupkg_WildcardFindsNothing_FileNotFoundError()
        {
            // Arrange
            using (var packageDirectory = TestDirectory.Create())
            {

                var nuget = Util.GetNuGetExePath();
                string snupkgToPush = "*.snupkg";

                using (var server = CreateAndStartMockV3Server(packageDirectory, out string sourceName))
                {
                    // Act
                    var result = CommandRunner.Run(
                        nuget,
                        packageDirectory,
                        $"push {snupkgToPush} -Source {sourceName} -Timeout 110 --debug",
                        waitForExit: true,
                        timeOutInMilliseconds: 120000); // 120 seconds

                    // Assert
                    server.Stop();

                    string expectedFileNotFoundErrorMessage = string.Format(MESSAGE_FILE_DOES_NOT_EXIST, snupkgToPush);

                    Assert.False(result.Success, "File did not exist and should fail.");
                    Assert.DoesNotContain(MESSAGE_PACKAGE_PUSHED, result.Output);
                    Assert.Contains(expectedFileNotFoundErrorMessage, result.Errors);
                }
            }
        }

        /// <summary>
        /// When pushing a nupkg by filename to a Symbol Server with no matching snupkg, do not show a File Not Found error. 
        /// </summary>
        [Fact]
        public void PushCommand_Server_Nupkg_FilenameSnupkgDoesNotExist_NoFileNotFoundError()
        {
            // Arrange
            using (var packageDirectory = TestDirectory.Create())
            {
                var nuget = Util.GetNuGetExePath();

                string packageId = "packageWithoutSnupkg";
                string version = "1.1.0";

                //Create Nupkg in test directory.
                string nupkgFullPath = Util.CreateTestPackage(packageId, version, packageDirectory);

                string nupkgFileName = Util.BuildPackageString(packageId, version, NuGetConstants.PackageExtension);
                string snupkgFileName = Util.BuildPackageString(packageId, version, NuGetConstants.SnupkgExtension);
                string snupkgFullPath = Path.Combine(packageDirectory, snupkgFileName);

                using (var server = CreateAndStartMockV3Server(packageDirectory, out string sourceName))
                {
                    SetupMockServerAlwaysCreate(server);

                    // Act
                    var result = CommandRunner.Run(
                        nuget,
                        packageDirectory,
                        $"push {nupkgFullPath} -Source {sourceName} -Timeout 110",
                        waitForExit: true,
                        timeOutInMilliseconds: 120000); // 120 seconds

                    // Assert
                    Assert.True(result.Success, "Expected to successfully push a nupkg without a snupkg.");
                    Assert.Contains(MESSAGE_PACKAGE_PUSHED, result.Output);
                    Assert.DoesNotContain(WITHOUT_FILENAME_MESSAGE_FILE_DOES_NOT_EXIST, result.Errors);
                }
            }
        }

        /// <summary>
        /// When pushing *.nupkg to a symbol server, but no snupkgs are selected with that wildcard, there is not a FileNotFound error about snupkgs.
        /// </summary>
        [Fact]
        public void PushCommand_Server_Nupkg_WildcardSnupkgDoesNotExist_FileNotFoundError()
        {
            // Arrange
            using (var packageDirectory = TestDirectory.Create())
            {
                var nuget = Util.GetNuGetExePath();
                string pushArgument = "*.nupkg";

                using (var server = CreateAndStartMockV3Server(packageDirectory, out string sourceName))
                {
                    // Act
                    var result = CommandRunner.Run(
                        nuget,
                        packageDirectory,
                        $"push {pushArgument} -Source {sourceName} -Timeout 110",
                        waitForExit: true,
                        timeOutInMilliseconds: 120000); // 120 seconds

                    // Assert
                    server.Stop();

                    Assert.True(result.Success, "Snupkg File did not exist but should not fail a nupkg push.");
                    Assert.Contains(MESSAGE_PACKAGE_PUSHED, result.Output);
                    Assert.DoesNotContain(WITHOUT_FILENAME_MESSAGE_FILE_DOES_NOT_EXIST, result.Errors);
                }
            }
        }

        /// <summary>
        /// When pushing a nupkg by filename to a Symbol Server with a matching snupkg, a 409 Conflict halts the push.
        /// TODO: bug fixes will come from https://github.com/NuGet/Home/issues/8148
        /// </summary>
        [Fact]
        public void PushCommand_Server_Nupkg_FilenameSnupkgExists_Conflict()
        {
            // Arrange
            using (var packageDirectory = TestDirectory.Create())
            {
                var nuget = Util.GetNuGetExePath();

                string packageId = "packageWithSnupkg";
                
                //Create nupkg in test directory.
                string version = "1.1.0";
                string nupkgFullPath = Util.CreateTestPackage(packageId, version, packageDirectory);
                string nupkgFileName = Util.BuildPackageString(packageId, version, NuGetConstants.PackageExtension);
                string snupkgFileName = Util.BuildPackageString(packageId, version, NuGetConstants.SnupkgExtension);
                string snupkgFullPath = Path.Combine(packageDirectory, snupkgFileName);
                //Create snupkg in test directory.
                WriteSnupkgFile(snupkgFullPath);

                using (var server = CreateAndStartMockV3Server(packageDirectory, out string sourceName))
                {
                    //Configure push to alternate returning Created and Conflict responses, which correspond to pushing the nupkg and snupkg, respectively.
                    SetupMockServerCreateNupkgDuplicateSnupkg(server, packageDirectory, FuncStatus_Alternates_CreatedAndDuplicate());

                    // Act

                    //Since this is V3, this will trigger 2 pushes: one for nupkgs, and one for snupkgs.
                    var result = CommandRunner.Run(
                        nuget,
                        packageDirectory,
                        $"push {nupkgFullPath} -Source {sourceName} -Timeout 110",
                        waitForExit: true,
                        timeOutInMilliseconds: 120000); // 120 seconds

                    //Second run with SkipDuplicate
                    var result2 = CommandRunner.Run(
                        nuget,
                        packageDirectory,
                        $"push {nupkgFullPath} -Source {sourceName} -Timeout 110 -SkipDuplicate",
                        waitForExit: true,
                        timeOutInMilliseconds: 120000); // 120 seconds

                    // Assert

                    //Ignoring filename in File Not Found error since the error should not appear in any case.
                    string genericFileNotFoundError = string.Format(MESSAGE_FILE_DOES_NOT_EXIST, string.Empty);

                    //Nupkg should push, but corresponding snupkg is a duplicate and errors.
                    Assert.False(0 == result.Item1, "Expected to fail push a due to duplicate snupkg.");
                    Assert.Contains(MESSAGE_PACKAGE_PUSHED, result.Item2); //nupkg pushed
                    Assert.Contains(MESSAGE_RESPONSE_NO_SUCCESS, result.AllOutput); //snupkg duplicate
                    Assert.DoesNotContain(genericFileNotFoundError, result.Item3);

                    //Nupkg should push, and corresponding snupkg is a duplicate.
                    //TODO: Once SkipDuplicate is passed-through to the inherit snupkg push, the following TODO's should be corrected.
                    Assert.False(0 == result2.Item1, "Expected to fail push with SkipDuplicate with a duplicate snupkg."); //TODO:  this should succeed and contain MESSAGE_EXISTING_PACKAGE.
                    Assert.Contains(MESSAGE_PACKAGE_PUSHED, result2.Item2); //nupkg pushed
                    Assert.DoesNotContain(MESSAGE_EXISTING_PACKAGE, result2.AllOutput); //snupkg duplicate TODO: DoesNotContain to Contains MESSAGE_EXISTING_PACKAGE

                    Assert.DoesNotContain(genericFileNotFoundError, result2.Item3);
                }
            }
        }

        /// <summary>
        /// When pushing *.Nupkg, (no skip duplicate) a 409 Conflict is returned and halts the secondary symbols push
        /// TODO: bug fixes will come from https://github.com/NuGet/Home/issues/8148
        /// </summary>
        [Fact]
        public void PushCommand_Server_Nupkg_WildcardFindsMatchingSnupkgs_Conflict()
        {
            // Arrange
            using (var packageDirectory = TestDirectory.Create())
            {
                var nuget = Util.GetNuGetExePath();

                string packageId = "packageWithSnupkg";

                //Create a nupkg in test directory.
                string version = "1.1.0";
                Util.CreateTestPackage(packageId, version, packageDirectory);
                string nupkgFileName = Util.BuildPackageString(packageId, version, NuGetConstants.PackageExtension);
                string snupkgFileName = Util.BuildPackageString(packageId, version, NuGetConstants.SnupkgExtension);
                string snupkgFullPath = Path.Combine(packageDirectory, snupkgFileName);
                //Create snupkg in test directory.
                WriteSnupkgFile(snupkgFullPath);

                string wildcardPush = "*.nupkg";

                using (var server = CreateAndStartMockV3Server(packageDirectory, out string sourceName))
                {
                    //Configure push to return a Conflict for the first push, then Created for all remaining pushes.
                    SetupMockServerCreateNupkgDuplicateSnupkg(server, packageDirectory, FuncStatus_Duplicate_ThenAlwaysCreated());

                    // Act

                    //Since this is V3, this will trigger 2 pushes: one for nupkgs, and one for snupkgs.
                    var result = CommandRunner.Run(
                        nuget,
                        packageDirectory,
                        $"push {wildcardPush} -Source {sourceName} -Timeout 110",
                        waitForExit: true,
                        timeOutInMilliseconds: 120000); // 120 seconds

                    // Assert

                    //Ignoring filename in File Not Found error since the error should not appear in any case.
                    string genericFileNotFoundError = string.Format(MESSAGE_FILE_DOES_NOT_EXIST, string.Empty);

                    //Nupkg should be a conflict, so its snupkg should also not push.
                    Assert.False(result.Success, "Expected to fail the push due to a duplicate nupkg.");
                    Assert.DoesNotContain(MESSAGE_PACKAGE_PUSHED, result.Output); //nothing pushed
                    Assert.Contains(MESSAGE_RESPONSE_NO_SUCCESS, result.Errors); //nupkg duplicate
                    Assert.DoesNotContain(genericFileNotFoundError, result.Errors);
                    Assert.DoesNotContain(".snupkg", result.AllOutput); //snupkg not mentioned
                }
            }
        }

        /// <summary>
        /// When pushing *.Nupkg with SkipDuplicate, a 409 Conflict is ignored and the secondary symbols push proceeds.
        /// TODO: bug fixes will come from https://github.com/NuGet/Home/issues/8148
        /// </summary>
        [Fact]
        public void PushCommand_Server_Nupkg_WildcardFindsMatchingSnupkgs_SkipDuplicate()
        {
            // Arrange
            using (var packageDirectory = TestDirectory.Create())
            {
                var nuget = Util.GetNuGetExePath();

                string packageId = "packageWithSnupkg";

                //Create a nupkg in test directory.
                string version = "1.1.0";
                Util.CreateTestPackage(packageId, version, packageDirectory);
                string nupkgFileName = Util.BuildPackageString(packageId, version, NuGetConstants.PackageExtension);
                string snupkgFileName = Util.BuildPackageString(packageId, version, NuGetConstants.SnupkgExtension);
                string snupkgFullPath = Path.Combine(packageDirectory, snupkgFileName);
                //Create snupkg in test directory.
                WriteSnupkgFile(snupkgFullPath);

                //Create another nupkg in test directory.
                version = "2.12.1";
                Util.CreateTestPackage(packageId, version, packageDirectory);
                string nupkgFileName2 = Util.BuildPackageString(packageId, version, NuGetConstants.PackageExtension);
                string snupkgFileName2 = Util.BuildPackageString(packageId, version, NuGetConstants.SnupkgExtension);
                string snupkgFullPath2 = Path.Combine(packageDirectory, snupkgFileName2);
                //Create another snupkg in test directory.
                WriteSnupkgFile(snupkgFullPath2);

                string wildcardPush = "*.nupkg";

                using (var server = CreateAndStartMockV3Server(packageDirectory, out string sourceName))
                {
                    SetupMockServerAlwaysDuplicate(server);

                    // Act

                    //Since this is V3, this will trigger 2 pushes: one for nupkgs, and one for snupkgs.
                    var result = CommandRunner.Run(
                        nuget,
                        packageDirectory,
                        $"push {wildcardPush} -Source {sourceName} -Timeout 110 -SkipDuplicate",
                        waitForExit: true,
                        timeOutInMilliseconds: 120000); // 120 seconds

                    // Assert

                    //Ignoring filename in File Not Found error since the error should not appear in any case.
                    string genericFileNotFoundError = string.Format(MESSAGE_FILE_DOES_NOT_EXIST, string.Empty);

                    //Nupkg should be an ignored conflict, so its snupkg should push.
                    //TODO: Once SkipDuplicate is passed-through to the inherit snupkg push, the following TODO's should be corrected.
                    Assert.False(result.Success, "Expected to fail to push a snupkg when the nupkg is a duplicate.");//TODO: False to True for Success (and "expected to successfully push...")
                    Assert.Contains(MESSAGE_RESPONSE_NO_SUCCESS, result.Errors); //nupkg duplicate TODO:  Contains from MESSAGE_RESPONSE_NO_SUCCESS to MESSAGE_EXISTING_PACKAGE
                    Assert.DoesNotContain(MESSAGE_PACKAGE_PUSHED, result.Output); //snupkgFileName and snupkgFileName2 pushed
                    Assert.DoesNotContain(genericFileNotFoundError, result.Errors);
                    Assert.Contains(snupkgFileName, result.AllOutput); //first snupkg is attempted as push
                    Assert.DoesNotContain(snupkgFileName2, result.AllOutput); //TODO: Should Contain second snupkg which is attempted when first duplicate is skipped.
                }
            }
        }

        /// <summary>
        /// When pushing *.Snupkg, (no skip duplicate) a 409 Conflict is returned and halts the remaining symbols push.
        /// TODO: bug fixes will come from https://github.com/NuGet/Home/issues/8148
        /// </summary>
        [Fact]
        public void PushCommand_Server_Snupkg_WildcardFindsMatchingSnupkgs_Conflict()
        {
            // Arrange
            using (var packageDirectory = TestDirectory.Create())
            {
                var nuget = Util.GetNuGetExePath();

                string packageId = "symbolsPackage";

                //Create a nupkg in test directory.
                string version = "1.1.0";
                Util.CreateTestPackage(packageId, version, packageDirectory);
                string nupkgFileName = Util.BuildPackageString(packageId, version, NuGetConstants.PackageExtension);
                string snupkgFileName = Util.BuildPackageString(packageId, version, NuGetConstants.SnupkgExtension);
                string snupkgFullPath = Path.Combine(packageDirectory, snupkgFileName);

                //Create snupkg in test directory.
                WriteSnupkgFile(snupkgFullPath);

                string wildcardPush = "*.snupkg";

                using (var server = CreateAndStartMockV3Server(packageDirectory, out string sourceName))
                {
                    //Configure push to return a Conflict for the first push, then Created for all remaining pushes.
                    SetupMockServerCreateNupkgDuplicateSnupkg(server, packageDirectory, FuncStatus_Duplicate_ThenAlwaysCreated());

                    // Act

                    //Since this is V3, this will trigger 2 pushes: one for nupkgs, and one for snupkgs.
                    var result = CommandRunner.Run(
                        nuget,
                        packageDirectory,
                        $"push {wildcardPush} -Source {sourceName} -Timeout 110",
                        waitForExit: true,
                        timeOutInMilliseconds: 120000); // 120 seconds

                    // Assert

                    //Ignoring filename in File Not Found error since the error should not appear in any case.
                    string genericFileNotFoundError = string.Format(MESSAGE_FILE_DOES_NOT_EXIST, string.Empty);

                    //Nupkg should be a conflict, so its snupkg should also not push.
                    Assert.False(result.Success, "Expected to fail the push due to a duplicate snupkg.");
                    Assert.DoesNotContain(MESSAGE_PACKAGE_PUSHED, result.Output); //nothing pushed
                    Assert.Contains(MESSAGE_RESPONSE_NO_SUCCESS, result.Errors); //nupkg duplicate
                    Assert.DoesNotContain(genericFileNotFoundError, result.Errors);
                    Assert.DoesNotContain(nupkgFileName, result.AllOutput); //nupkg not mentioned
                }
            }
        }

        /// <summary>
        /// When pushing *.Snupkg with SkipDuplicate, a 409 Conflict is ignored and the remaining symbols push proceeds.
        /// TODO: bug fixes will come from https://github.com/NuGet/Home/issues/8148
        /// </summary>
        [Fact]
        public void PushCommand_Server_Snupkg_WildcardFindsMatchingSnupkgs_SkipDuplicate()
        {
            // Arrange
            using (var packageDirectory = TestDirectory.Create())
            {
                var nuget = Util.GetNuGetExePath();

                string packageId = "packageWithSnupkg";

                //Create a nupkg in test directory.
                string version = "1.1.0";
                Util.CreateTestPackage(packageId, version, packageDirectory);
                string nupkgFileName = Util.BuildPackageString(packageId, version, NuGetConstants.PackageExtension);
                string snupkgFileName = Util.BuildPackageString(packageId, version, NuGetConstants.SnupkgExtension);
                string snupkgFullPath = Path.Combine(packageDirectory, snupkgFileName);
                //Create snupkg in test directory.
                WriteSnupkgFile(snupkgFullPath);

                //Create another nupkg in test directory.
                version = "2.12.1";
                Util.CreateTestPackage(packageId, version, packageDirectory);
                string nupkgFileName2 = Util.BuildPackageString(packageId, version, NuGetConstants.PackageExtension);
                string snupkgFileName2 = Util.BuildPackageString(packageId, version, NuGetConstants.SnupkgExtension);
                string snupkgFullPath2 = Path.Combine(packageDirectory, snupkgFileName2);
                //Create another snupkg in test directory.
                WriteSnupkgFile(snupkgFullPath2);

                string wildcardPush = "*.snupkg";

                using (var server = CreateAndStartMockV3Server(packageDirectory, out string sourceName))
                {
                    SetupMockServerAlwaysDuplicate(server);

                    // Act

                    //Since this is V3, this will trigger 2 pushes: one for nupkgs, and one for snupkgs.
                    var result = CommandRunner.Run(
                        nuget,
                        packageDirectory,
                        $"push {wildcardPush} -Source {sourceName} -Timeout 110 -SkipDuplicate",
                        waitForExit: true,
                        timeOutInMilliseconds: 120000); // 120 seconds

                    // Assert

                    //Ignoring filename in File Not Found error since the error should not appear in any case.
                    string genericFileNotFoundError = string.Format(MESSAGE_FILE_DOES_NOT_EXIST, string.Empty);

                    //Nupkg should be an ignored conflict, so its snupkg should push.
                    //TODO: Once SkipDuplicate is passed-through to the inherit snupkg push, the following TODO's should be corrected.
                    Assert.False(result.Success, "Expected to fail to push a snupkg when the nupkg is a duplicate."); //TODO: Should succeed since duplicates are skipped.
                    Assert.Contains(MESSAGE_RESPONSE_NO_SUCCESS, result.Errors); //TODO: snupkg duplicate is ignored
                    Assert.DoesNotContain(MESSAGE_PACKAGE_PUSHED, result.Output); //snupkgFileName and snupkgFileName2 pushed
                    Assert.DoesNotContain(MESSAGE_EXISTING_PACKAGE, result.Errors); //TODO: Expect this message once SkipDuplicate is working.
                    Assert.DoesNotContain(genericFileNotFoundError, result.Errors);

                    Assert.Contains(snupkgFileName, result.AllOutput); //first snupkg is attempted as push
                    Assert.DoesNotContain(snupkgFileName2, result.AllOutput); //TODO: second snupkg should be attempted.

                    Assert.DoesNotContain(nupkgFileName, result.AllOutput); //nupkgs should not be attempted in push
                    Assert.DoesNotContain(nupkgFileName2, result.AllOutput); //nupkgs should not be attempted in push
                }
            }
        }


        /// <summary>
        /// When pushing snupkg with SkipDuplicate, a 409 Conflict is ignored and any message from the server is shown appropriately.
        /// TODO: bug fixes will come from https://github.com/NuGet/Home/issues/8148
        /// </summary>
        [Fact]
        public void PushCommand_Server_Snupkg_FilenameSnupkgExists_SkipDuplicate_ServerMessage()
        {
            // Arrange
            using (var packageDirectory = TestDirectory.Create())
            {
                var nuget = Util.GetNuGetExePath();

                string snupkgFileName = "fileName.snupkg";
                string snupkgFullPath = Path.Combine(packageDirectory, snupkgFileName);
                //Create snupkg in test directory.
                WriteSnupkgFile(snupkgFullPath);

                using (var server = CreateAndStartMockV3Server(packageDirectory, out string sourceName))
                {
                    SetupMockServerAlwaysDuplicate(server);

                    // Act

                    //Since this is V3, this will trigger 2 pushes: one for nupkgs, and one for snupkgs.
                    var result = CommandRunner.Run(
                        nuget,
                        packageDirectory,
                        $"push {snupkgFileName} -Source {sourceName} -Timeout 110",
                        waitForExit: true,
                        timeOutInMilliseconds: 120000); // 120 seconds

                    // Assert
                    Assert.False(result.Success, "Expected a Duplicate response to fail the push.");
                    Assert.Contains("Conflict", result.AllOutput);
                }
            }

        }

        #region Helpers
        /// <summary>
        /// Sets up the server for the steps of running 3 Push commands. First is the initial push, followed by a duplicate push, followed by a new package push.
        /// Depending on the options of the push, the duplicate will either be a warning or an error and permit or prevent the third push.
        /// </summary>
        /// <param name="server">Server object to modify.</param>
        /// <param name="outputPathFunc">Function to determine path to output package.</param>
        /// <param name="responseCodeFunc">Function to determine which HttpStatusCode to return.</param>
        private static void SetupMockServerForSkipDuplicate(MockServer server,
                                                              Func<int, string> outputPathFunc,
                                                              Func<int, HttpStatusCode> responseCodeFunc)
        {
            int packageCounter = 0;
            server.Put.Add("/push", (Func<HttpListenerRequest, object>)((r) =>
            {
                packageCounter++;
                var outputPath = outputPathFunc(packageCounter);

                MockServer.SavePushedPackage(r, outputPath);

                return responseCodeFunc(packageCounter);
            }));
        }

        private static void SetupMockServerAlwaysDuplicate(MockServer server)
        {
            server.Put.Add("/push", (Func<HttpListenerRequest, object>)((r) =>
            {
                return HttpStatusCode.Conflict;
            }));
        }

        private static void SetupMockServerAlwaysCreate(MockServer server)
        {
            server.Put.Add("/push", (Func<HttpListenerRequest, object>)((r) =>
            {
                return HttpStatusCode.Created;
            }));
        }


        private static void WriteSnupkgFile(string snupkgFullPath)
        {
            FileStream fileSnupkg = null;
            try
            {
                fileSnupkg = File.Create(snupkgFullPath);
            }
            finally
            {
                if (fileSnupkg != null)
                {
                    fileSnupkg.Flush();
                    fileSnupkg.Close();
                }
            }
        }


        private static void SetupMockServerCreateNupkgDuplicateSnupkg(MockServer server,
                                                              string outputPath,
                                                              Func<int, HttpStatusCode> responseCodeFunc)
        {
            int packageCounter = 0;
            server.Put.Add("/push", (Func<HttpListenerRequest, object>)((r) =>
            {
                packageCounter++;
                var statusCode = responseCodeFunc(packageCounter);
                return statusCode;
            }));
        }

        /// <summary>
        /// Switches to the second path on the 3rd count.
        /// </summary>
        private static Func<int, string> FuncOutputPath_SwitchesOnThirdPush(string outputPath, string outputPath2)
        {
            return (count) =>
            {
                if (count >= 3)
                {
                    return outputPath2;
                }
                return outputPath;
            };
        }

        /// <summary>
        /// Status is Created except for 2nd count which is fixed as a Conflict.
        /// </summary>
        private static Func<int, HttpStatusCode> FuncStatusDuplicate_OccursOnSecondPush()
        {
            return (count) =>
            {
                //Second run will be treated as duplicate.
                if (count == 2)
                {
                    return HttpStatusCode.Conflict;
                }
                else
                {
                    return HttpStatusCode.Created;
                }
            };
        }

        /// <summary>
        /// Status alternates between Created and Conflict, (divisible by 2 is a Conflict by default).
        /// </summary>
        private static Func<int, HttpStatusCode> FuncStatus_Alternates_CreatedAndDuplicate(bool startWithCreated = true)
        {
            var firstResponse = startWithCreated ? HttpStatusCode.Created : HttpStatusCode.Conflict;
            var secondResponse = startWithCreated ? HttpStatusCode.Conflict : HttpStatusCode.Created;

            return (count) =>
            {
                //Every second run will be the opposite of the previous run.
                if (count % 2 == 0)
                {
                    return secondResponse;
                }
                else
                {
                    return firstResponse;
                }
            };
        }

        /// <summary>
        /// Status is first Duplicate followed by all Created.
        /// </summary>
        private static Func<int, HttpStatusCode> FuncStatus_Duplicate_ThenAlwaysCreated()
        {
            return (count) =>
            {
                if (count == 1)
                {
                    return HttpStatusCode.Conflict;
                }
                else
                {
                    return HttpStatusCode.Created;
                }
            };
        }

        /// <summary>
        /// Creates a V3 Mock Server that supports Publish and Symbol Server.
        /// </summary>
        /// <param name="packageDirectory">Path where this server should write (eg, nuget.config).</param>
        /// <param name="sourceName">URI for index.json</param>
        /// <returns></returns>
        private static MockServer CreateAndStartMockV3Server(string packageDirectory, out string sourceName)
        {
            var server = new MockServer();
            var indexJson = Util.CreateIndexJson();

            Util.AddPublishResource(indexJson, server);
            server.Get.Add("/", r =>
            {
                var path = server.GetRequestUrlAbsolutePath(r);
                if (path == "/index.json")
                {
                    return new Action<HttpListenerResponse>(response =>
                    {
                        response.StatusCode = 200;
                        response.ContentType = "text/javascript";
                        MockServer.SetResponseContent(response, indexJson.ToString());
                    });
                }

                throw new Exception("This test needs to be updated to support: " + path);
            });

            server.Start();

            var sources = new List<string>();
            sourceName = $"{server.Uri}index.json";
            sources.Add(sourceName);

            if (!string.IsNullOrWhiteSpace(packageDirectory))
            {
                Util.CreateNuGetConfig(packageDirectory, sources);
            }
            Util.AddPublishSymbolsResource(indexJson, server);

            return server;
        }

        #endregion
    }
}
