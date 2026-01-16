using System;
using System.Threading.Tasks;
using Playnite.SDK;

namespace FriendsAchievementFeed.Common
{
    /// <summary>
    /// Provides reusable error handling patterns to reduce duplicate catch blocks throughout the codebase.
    /// </summary>
    internal static class ErrorHandlingHelpers
    {
        /// <summary>
        /// Executes an async operation with standardized error handling and logging.
        /// </summary>
        /// <typeparam name="T">The return type of the operation</typeparam>
        /// <param name="operation">The async operation to execute</param>
        /// <param name="logger">The logger instance (can be null)</param>
        /// <param name="errorMessage">The message to log on error</param>
        /// <param name="defaultValue">The value to return if an error occurs</param>
        /// <returns>The result of the operation, or defaultValue on error</returns>
        public static async Task<T> TryExecuteAsync<T>(
            Func<Task<T>> operation,
            ILogger logger,
            string errorMessage,
            T defaultValue = default)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Let cancellation bubble up - don't log as error
                throw;
            }
            catch (Exception ex)
            {
                logger?.Error(ex, errorMessage);
                return defaultValue;
            }
        }

        /// <summary>
        /// Executes an async operation with standardized error handling and logging (no return value).
        /// </summary>
        /// <param name="operation">The async operation to execute</param>
        /// <param name="logger">The logger instance (can be null)</param>
        /// <param name="errorMessage">The message to log on error</param>
        public static async Task TryExecuteAsync(
            Func<Task> operation,
            ILogger logger,
            string errorMessage)
        {
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Let cancellation bubble up - don't log as error
                throw;
            }
            catch (Exception ex)
            {
                logger?.Error(ex, errorMessage);
            }
        }

        /// <summary>
        /// Executes a synchronous operation with standardized error handling and logging.
        /// </summary>
        /// <typeparam name="T">The return type of the operation</typeparam>
        /// <param name="operation">The operation to execute</param>
        /// <param name="logger">The logger instance (can be null)</param>
        /// <param name="errorMessage">The message to log on error</param>
        /// <param name="defaultValue">The value to return if an error occurs</param>
        /// <returns>The result of the operation, or defaultValue on error</returns>
        public static T TryExecute<T>(
            Func<T> operation,
            ILogger logger,
            string errorMessage,
            T defaultValue = default)
        {
            try
            {
                return operation();
            }
            catch (Exception ex)
            {
                logger?.Error(ex, errorMessage);
                return defaultValue;
            }
        }

        /// <summary>
        /// Executes a synchronous operation with standardized error handling and logging (no return value).
        /// </summary>
        /// <param name="operation">The operation to execute</param>
        /// <param name="logger">The logger instance (can be null)</param>
        /// <param name="errorMessage">The message to log on error</param>
        public static void TryExecute(
            Action operation,
            ILogger logger,
            string errorMessage)
        {
            try
            {
                operation();
            }
            catch (Exception ex)
            {
                logger?.Error(ex, errorMessage);
            }
        }

        /// <summary>
        /// Logs an error with the provided logger.
        /// </summary>
        /// <param name="logger">The logger instance (can be null)</param>
        /// <param name="ex">The exception to log</param>
        /// <param name="message">The error message</param>
        public static void LogError(ILogger logger, Exception ex, string message)
        {
            logger?.Error(ex, message);
        }
    }
}
