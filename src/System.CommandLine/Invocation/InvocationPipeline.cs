﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading;
using System.Threading.Tasks;

namespace System.CommandLine.Invocation
{
    internal static class InvocationPipeline
    {
        internal static async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            if (parseResult.Action is null)
            {
                return ReturnCodeForMissingAction(parseResult);
            }

            ProcessTerminationHandler? terminationHandler = null;
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                if (parseResult.PreActions is not null)
                {
                    for (int i = 0; i < parseResult.PreActions.Count; i++)
                    {
                        var action = parseResult.PreActions[i];

                        switch (action)
                        {
                            case SynchronousCommandLineAction syncAction:
                                syncAction.Invoke(parseResult);
                                break;
                            case AsynchronousCommandLineAction asyncAction:
                                await asyncAction.InvokeAsync(parseResult, cts.Token);
                                break;
                        }
                    }
                }

                switch (parseResult.Action)
                {
                    case SynchronousCommandLineAction syncAction:
                        return syncAction.Invoke(parseResult);

                    case AsynchronousCommandLineAction asyncAction:
                        var startedInvocation = asyncAction.InvokeAsync(parseResult, cts.Token);

                        var timeout = parseResult.InvocationConfiguration.ProcessTerminationTimeout;

                        if (timeout.HasValue)
                        {
                            terminationHandler = new(cts, startedInvocation, timeout.Value);
                        }

                        if (terminationHandler is null)
                        {
                            return await startedInvocation;
                        }
                        else
                        {
                            // Handlers may not implement cancellation.
                            // In such cases, when CancelOnProcessTermination is configured and user presses Ctrl+C,
                            // ProcessTerminationCompletionSource completes first, with the result equal to native exit code for given signal.
                            Task<int> firstCompletedTask = await Task.WhenAny(startedInvocation, terminationHandler.ProcessTerminationCompletionSource.Task);
                            return await firstCompletedTask; // return the result or propagate the exception
                        }

                    default:
                        throw new ArgumentOutOfRangeException(nameof(parseResult.Action));
                }
            }
            catch (Exception ex) when (parseResult.InvocationConfiguration.EnableDefaultExceptionHandler)
            {
                return DefaultExceptionHandler(ex, parseResult);
            }
            finally
            {
                terminationHandler?.Dispose();
            }
        }

        internal static int Invoke(ParseResult parseResult)
        {
            switch (parseResult.Action)
            {
                case null:
                    return ReturnCodeForMissingAction(parseResult);

                case SynchronousCommandLineAction syncAction:
                    try
                    {
                        if (parseResult.PreActions is not null)
                        {
#if DEBUG
                            for (var i = 0; i < parseResult.PreActions.Count; i++)
                            {
                                var action = parseResult.PreActions[i];

                                if (action is not SynchronousCommandLineAction)
                                {
                                    parseResult.InvocationConfiguration.EnableDefaultExceptionHandler = false;
                                    throw new Exception(
                                        $"This should not happen. An instance of {nameof(AsynchronousCommandLineAction)} ({action}) was called within {nameof(InvocationPipeline)}.{nameof(Invoke)}. This is supposed to be detected earlier resulting in a call to {nameof(InvocationPipeline)}{nameof(InvokeAsync)}");
                                }
                            }
#endif

                            for (var i = 0; i < parseResult.PreActions.Count; i++)
                            {
                                if (parseResult.PreActions[i] is SynchronousCommandLineAction syncPreAction)
                                {
                                    syncPreAction.Invoke(parseResult);
                                }
                            }
                        }

                        return syncAction.Invoke(parseResult);
                    }
                    catch (Exception ex) when (parseResult.InvocationConfiguration.EnableDefaultExceptionHandler)
                    {
                        return DefaultExceptionHandler(ex, parseResult);
                    }

                default:
                    throw new InvalidOperationException($"{nameof(AsynchronousCommandLineAction)} called within non-async invocation.");
            }
        }

        private static int DefaultExceptionHandler(Exception exception, ParseResult parseResult)
        {
            if (exception is not OperationCanceledException)
            {
                ConsoleHelpers.ResetTerminalForegroundColor();
                ConsoleHelpers.SetTerminalForegroundRed();

                var error = parseResult.InvocationConfiguration.Error;

                error.Write(LocalizationResources.ExceptionHandlerHeader());
                error.WriteLine(exception.ToString());

                ConsoleHelpers.ResetTerminalForegroundColor();
            }
            return 1;
        }

        private static int ReturnCodeForMissingAction(ParseResult parseResult)
        {
            if (parseResult.Errors.Count > 0)
            {
                return 1;
            }
            else
            {
                return 0;
            }
        }
    }
}
