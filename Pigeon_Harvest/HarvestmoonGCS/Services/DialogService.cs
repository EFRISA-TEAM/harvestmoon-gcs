using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using HarvestmoonGCS.Core.Services;

namespace HarvestmoonGCS.Services
{
    /// <summary>
    /// Hybrid DialogService implementation for Uno Platform
    /// Uses ContentDialog for Desktop, platform-specific implementations for mobile
    /// </summary>
    public class DialogService : IDialogService
    {
        private static readonly System.Threading.SemaphoreSlim _dialogGate = new(1, 1);

        /// <summary>
        /// Shows an alert dialog with OK button
        /// </summary>
        public async Task ShowAlertAsync(string message, string title = "Alert")
        {
            if (!await _dialogGate.WaitAsync(0))
            {
                Serilog.Log.Warning("[DialogService] Suppressed concurrent alert: {Title} - {Message}", title, message);
                return;
            }

            try
            {
                var xamlRoot = GetXamlRoot();
                if (xamlRoot == null)
                {
                    Serilog.Log.Warning("[DialogService] Cannot show alert: XamlRoot is null. Message: {Message}", message);
                    return;
                }

                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    CloseButtonText = "OK",
                    XamlRoot = xamlRoot
                };

                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "[DialogService] ShowAlertAsync error");
            }
            finally
            {
                _dialogGate.Release();
            }
        }

        /// <summary>
        /// Shows a confirmation dialog with Yes/No buttons
        /// </summary>
        public async Task<bool> ShowConfirmAsync(string message, string title = "Confirm")
        {
            if (!await _dialogGate.WaitAsync(0))
            {
                Serilog.Log.Warning("[DialogService] Suppressed concurrent confirm: {Title} - {Message}", title, message);
                return false;
            }

            try
            {
                var xamlRoot = GetXamlRoot();
                if (xamlRoot == null)
                {
                    Serilog.Log.Warning("[DialogService] Cannot show confirm: XamlRoot is null");
                    return false;
                }

                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    PrimaryButtonText = "Yes",
                    CloseButtonText = "No",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = xamlRoot
                };

                var result = await dialog.ShowAsync();
                return result == ContentDialogResult.Primary;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "[DialogService] ShowConfirmAsync error");
                return false;
            }
            finally
            {
                _dialogGate.Release();
            }
        }

        /// <summary>
        /// Shows a custom dialog and returns the result
        /// </summary>
        public async Task<T?> ShowDialogAsync<T>() where T : class
        {
            if (!await _dialogGate.WaitAsync(0))
            {
                Serilog.Log.Warning("[DialogService] Suppressed concurrent dialog of type {Type}", typeof(T).Name);
                return null;
            }

            try
            {
                var dialog = Activator.CreateInstance<T>();
                
                if (dialog is ContentDialog contentDialog)
                {
                    var xamlRoot = GetXamlRoot();
                    if (xamlRoot == null) return null;
                    contentDialog.XamlRoot = xamlRoot;
                    var result = await contentDialog.ShowAsync();
                    
                    var resultProperty = typeof(T).GetProperty("Result");
                    if (resultProperty != null && result == ContentDialogResult.Primary)
                    {
                        return resultProperty.GetValue(dialog) as T;
                    }
                    
                    return result == ContentDialogResult.Primary ? dialog : null;
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "[DialogService] ShowDialogAsync error");
                return null;
            }
            finally
            {
                _dialogGate.Release();
            }
        }

        /// <summary>
        /// Shows a custom dialog with a parameter and returns the result
        /// </summary>
        public async Task<TResult?> ShowDialogAsync<T, TParam, TResult>(TParam parameter) 
            where T : class 
            where TResult : class
        {
            if (!await _dialogGate.WaitAsync(0))
            {
                Serilog.Log.Warning("[DialogService] Suppressed concurrent dialog of type {Type}", typeof(T).Name);
                return null;
            }

            try
            {
                var dialog = Activator.CreateInstance(typeof(T), parameter);
                
                if (dialog is ContentDialog contentDialog)
                {
                    var xamlRoot = GetXamlRoot();
                    if (xamlRoot == null) return null;
                    contentDialog.XamlRoot = xamlRoot;
                    var result = await contentDialog.ShowAsync();
                    
                    var resultProperty = typeof(T).GetProperty("Result");
                    if (resultProperty != null && result == ContentDialogResult.Primary)
                    {
                        return resultProperty.GetValue(dialog) as TResult;
                    }
                    
                    return null;
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "[DialogService] ShowDialogAsync error");
                return null;
            }
            finally
            {
                _dialogGate.Release();
            }
        }

        /// <summary>
        /// Gets the XamlRoot from the main window
        /// </summary>
        private Microsoft.UI.Xaml.XamlRoot? GetXamlRoot()
        {
            return App.MainWindow?.Content?.XamlRoot;
        }
    }
}
