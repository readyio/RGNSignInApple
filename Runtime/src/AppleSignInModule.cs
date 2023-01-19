using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Text;
using Firebase.Auth;
using RGN.Utility;
using Firebase.Extensions;
using Firebase;

#if PLATFORM_IOS
using AppleAuth;
using AppleAuth.Enums;
using AppleAuth.Extensions;
using AppleAuth.Interfaces;
using AppleAuth.Native;
#endif

namespace RGN.Modules.SignIn
{
    public class AppleSignInModule : BaseModule<AppleSignInModule>, IRGNModule
    {
        private IRGNRolesCore rgnCore;

#if PLATFORM_IOS
        private static AppleAuthManager appleAuthManager;
#endif

        public void SetRGNCore(IRGNRolesCore rgnCore)
        {
            this.rgnCore = rgnCore;
        }

        public void Init()
        {
#if PLATFORM_IOS && !UNITY_EDITOR
            if (AppleAuthManager.IsCurrentPlatformSupported)
            {
                // Creates a default JSON deserializer, to transform JSON Native responses to C# instances
                var deserializer = new PayloadDeserializer();
                // Creates an Apple Authentication manager with the deserializer
                appleAuthManager = new AppleAuthManager(deserializer);
            }

            rgnCore.OnUpdate += appleAuthManager.Update;
#endif
        }
        public void Dispose()
        {
#if PLATFORM_IOS && !UNITY_EDITOR
            rgnCore.OnUpdate -= appleAuthManager.Update;
#endif
        }

        public void TryToSignIn(bool tryToLinkToCurrentAccount = false)
        {
#if PLATFORM_IOS && !UNITY_EDITOR
            var rawNonce = NonceGenerator.GenerateRandomString(32);
            var nonce = NonceGenerator.GenerateSHA256NonceFromRawNonce(rawNonce);

            var loginArgs = new AppleAuthLoginArgs(LoginOptions.IncludeEmail | LoginOptions.IncludeFullName, nonce);
            appleAuthManager.LoginWithAppleId(loginArgs, credential =>
            {
                Debug.Log($"[AppleSignInModule]: APPLE, login SUCCESS {credential.User} ");

                var appleIdCredential = credential as IAppleIDCredential;
                if (appleIdCredential != null)
                {
                    Debug.Log($"[AppleSignInModule]: APPLE, login email {appleIdCredential.Email}");

                    if (tryToLinkToCurrentAccount)
                    {
                        rgnCore.CanTheUserBeLinkedAsync(appleIdCredential.Email).ContinueWithOnMainThread(checkLinkResult =>
                        {
                            if (checkLinkResult.IsCanceled)
                            {
                                rgnCore.Dependencies.Logger.LogWarning("[AppleSignInModule]: CanTheUserBeLinkedAsync was cancelled");
                                SignOutFromApple();
                                return;
                            }

                            if (checkLinkResult.IsFaulted)
                            {
                                Utility.ExceptionHelper.PrintToLog(rgnCore.Dependencies.Logger, checkLinkResult.Exception);
                                SignOutFromApple();
                                rgnCore.SetAuthCompletion(EnumLoginState.Error, EnumLoginError.Unknown);
                                return;
                            }

                            bool canBeLinked = (bool) checkLinkResult.Result.Data;
                            if (!canBeLinked)
                            {
                                rgnCore.Dependencies.Logger.LogError("[AppleSignInModule]: The user can not be linked");
                                SignOutFromApple();
                                rgnCore.SetAuthCompletion(EnumLoginState.Error, EnumLoginError.AccountAlreadyLinked);
                                return;
                            }
                            
                            LinkAppleAccountToFirebase(appleIdCredential, rawNonce);
                        });
                    }
                    else
                    {
                        SignInWithAppleOnFirebase(appleIdCredential, rawNonce);
                    }
                }
            }, error =>
            {
                Debug.Log("[AppleSignInModule]: APPLE, login Canceled ");
                
                rgnCore.SetAuthCompletion(EnumLoginState.Error, EnumLoginError.Unknown);
            });
#endif
        }

#if PLATFORM_IOS && !UNITY_EDITOR
        private void LinkAppleAccountToFirebase(IAppleIDCredential appleIdCredential, string rawNonce)
        {
            Debug.Log("LinkAppleAccountToFirebase");

            var identityToken = Encoding.UTF8.GetString(appleIdCredential.IdentityToken);
            var authorizationCode = Encoding.UTF8.GetString(appleIdCredential.AuthorizationCode);
            var firebaseCredential = rgnCore.ReadyMasterAuth.oAuthProvider.GetCredential("apple.com", identityToken, rawNonce, authorizationCode);

            rgnCore.Auth.CurrentUser.LinkAndRetrieveDataWithCredentialAsync(firebaseCredential).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled)
                {
                    rgnCore.Dependencies.Logger.LogWarning("[AppleSignInModule]: LinkAndRetrieveDataWithCredentialAsync was cancelled");
                    return;
                }
                
                if (task.IsFaulted)
                {
                    Utility.ExceptionHelper.PrintToLog(rgnCore.Dependencies.Logger, task.Exception);
                    FirebaseAccountLinkException firebaseAccountLinkException = task.Exception.InnerException as FirebaseAccountLinkException;
                    if (firebaseAccountLinkException != null && firebaseAccountLinkException.ErrorCode == (int)AuthError.CredentialAlreadyInUse)
                    {
                        rgnCore.SetAuthCompletion(EnumLoginState.Error, EnumLoginError.AccountAlreadyLinked);
                        return;
                    }

                    FirebaseException firebaseException = task.Exception.InnerException as FirebaseException;
                    
                    if (firebaseException != null)
                    {
                        EnumLoginError loginError = (AuthError)firebaseException.ErrorCode switch {
                            AuthError.EmailAlreadyInUse => EnumLoginError.AccountAlreadyLinked,
                            AuthError.RequiresRecentLogin => EnumLoginError.AccountNeedsRecentLogin,
                            _ => EnumLoginError.Unknown
                        };
                        
                        rgnCore.SetAuthCompletion(EnumLoginState.Error, loginError);
                        return;
                    }

                    rgnCore.SetAuthCompletion(EnumLoginState.Error, EnumLoginError.Unknown);
                    return;
                }

                Debug.Log("[AppleSignInModule]: LinkWith Apple Successful.");
                
                rgnCore.Auth.CurrentUser.TokenAsync(false).ContinueWithOnMainThread(taskAuth =>
                {
                    if (taskAuth.IsCanceled)
                    {
                        rgnCore.Dependencies.Logger.LogWarning("[AppleSignInModule]: TokenAsync was cancelled");
                        SignOutFromApple();
                        return;
                    }
                    
                    if (taskAuth.IsFaulted)
                    {
                        Utility.ExceptionHelper.PrintToLog(rgnCore.Dependencies.Logger, taskAuth.Exception);
                        SignOutFromApple();
                        rgnCore.SetAuthCompletion(EnumLoginState.Error, EnumLoginError.Unknown);
                        return;
                    }

                    rgnCore.LinkWithProviderAsync(false, taskAuth.Result).ContinueWithOnMainThread(taskLink =>
                    {
                        rgnCore.SetAuthCompletion(EnumLoginState.Success, EnumLoginError.Ok);
                    });
                });
            });
        }

        private void SignInWithAppleOnFirebase(IAppleIDCredential appleIdCredential, string rawNonce)
        {
            Debug.Log("SignInWithAppleOnFirebase");

            var identityToken = Encoding.UTF8.GetString(appleIdCredential.IdentityToken);
            var authorizationCode = Encoding.UTF8.GetString(appleIdCredential.AuthorizationCode);
            var firebaseCredential = rgnCore.ReadyMasterAuth.oAuthProvider.GetCredential("apple.com", identityToken, rawNonce, authorizationCode);

            rgnCore.Auth.SignInWithCredentialAsync(firebaseCredential).ContinueWithOnMainThread(task =>
            {
                Debug.LogFormat("[AppleSignInModule]: SignInWithCredentialAsync, isCanceled: {0}, isFaulted: {1}", task.IsCanceled, task.IsFaulted);

                if (task.IsCanceled)
                {
                    rgnCore.Dependencies.Logger.LogWarning("[AppleSignInModule]: SignInWithCredentialAsync was cancelled");
                    SignOutFromApple();
                    return;
                }
                
                if (task.IsFaulted)
                {
                    Utility.ExceptionHelper.PrintToLog(rgnCore.Dependencies.Logger, task.Exception);
                    rgnCore.SetAuthCompletion(EnumLoginState.Error, EnumLoginError.Unknown);
                    return;
                }

                Debug.Log("[AppleSignInModule]: Sign In with Apple Successful." + task.Result.UserId);
                
                task.Result.TokenAsync(false).ContinueWithOnMainThread(taskToken =>
                {
                    if (taskToken.IsCanceled)
                    {
                        rgnCore.Dependencies.Logger.LogWarning("[AppleSignInModule]: TokenAsync was cancelled");
                        return;
                    }
                    
                    if (taskToken.IsFaulted)
                    {
                        Utility.ExceptionHelper.PrintToLog(rgnCore.Dependencies.Logger, taskToken.Exception);
                        rgnCore.SetAuthCompletion(EnumLoginState.Error, EnumLoginError.Unknown);
                        return;
                    }

                    Debug.Log("[AppleSignInModule]: Apple, userToken " + taskToken.Result);
                    
                    rgnCore.CreateCustomTokenAsync(taskToken.Result).ContinueWithOnMainThread(taskCustom =>
                    {
                        if (taskCustom.IsCanceled)
                        {
                            rgnCore.Dependencies.Logger.LogWarning("[AppleSignInModule]: CreateCustomTokenAsync was cancelled");
                            return;
                        }
                        
                        if (taskCustom.IsFaulted)
                        {
                            Utility.ExceptionHelper.PrintToLog(rgnCore.Dependencies.Logger, taskCustom.Exception);
                            rgnCore.SetAuthCompletion(EnumLoginState.Error, EnumLoginError.Unknown);
                            return;
                        }
                        
                        if (string.IsNullOrEmpty(taskCustom.Result))
                        {
                            rgnCore.Dependencies.Logger.LogWarning("[AppleSignInModule]: CreateCustomTokenAsync result is null or empty");
                            rgnCore.SetAuthCompletion(EnumLoginState.Error, EnumLoginError.Unknown);
                            return;
                        }

                        Debug.Log("[AppleSignInModule]: Apple, masterToken " + taskCustom.Result);
                        
                        rgnCore.ReadyMasterAuth.SignInWithCustomTokenAsync(taskCustom.Result);
                    });
                });
            });
        }
#endif

        public void SignOut()
        {
            rgnCore.SignOutRGN();
        }
    }
}
