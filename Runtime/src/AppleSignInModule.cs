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

namespace RGN.Modules
{
    public class AppleSignInModule : IRGNModule
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

        public void OnSignInWithApple(bool isLink = false)
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

                    if (isLink)
                    {
                        rgnCore.IsUserCanBeLinked(appleIdCredential.Email).ContinueWithOnMainThread(checkLinkResult =>
                        {
                            if (checkLinkResult.IsCanceled)
                            {
                                SignOutFromApple();
                                return;
                            }
                    
                            if (checkLinkResult.IsFaulted)
                            {
                                SignOutFromApple();
                                rgnCore.SetAuthCompletion(EnumLoginState.Error, EnumLoginError.Unknown);
                                return;
                            }

                            bool canBeLinked = (bool) checkLinkResult.Result.Data;
                            if (!canBeLinked)
                            {
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
            var firebaseCredential = rgnCore.readyMasterAuth.oAuthProvider.GetCredential("apple.com", identityToken, rawNonce, authorizationCode);

            rgnCore.auth.CurrentUser.LinkAndRetrieveDataWithCredentialAsync(firebaseCredential).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled)
                {
                    return;
                }
                
                if (task.IsFaulted)
                {
                    FirebaseAccountLinkException firebaseAccountLinkException = task.Exception.InnerException as FirebaseAccountLinkException;
                    if (firebaseAccountLinkException != null && firebaseAccountLinkException.ErrorCode == (int)AuthError.CredentialAlreadyInUse)
                    {
                        rgnCore.SetAuthCompletion(EnumLoginState.Error, EnumLoginError.AccountAlreadyLinked);
                        return;
                    }

                    FirebaseException firebaseException = task.Exception.InnerException as FirebaseException;
                    if (firebaseException != null && firebaseException.ErrorCode == (int)AuthError.EmailAlreadyInUse)
                    {
                        rgnCore.SetAuthCompletion(EnumLoginState.Error, EnumLoginError.AccountAlreadyLinked);
                        return;
                    }

                    rgnCore.SetAuthCompletion(EnumLoginState.Error, EnumLoginError.Unknown);
                    return;
                }

                Debug.Log("[AppleSignInModule]: LinkWith Apple Successful.");
                
                rgnCore.auth.CurrentUser.TokenAsync(false).ContinueWithOnMainThread(taskAuth =>
                {
                    if (taskAuth.IsCanceled)
                    {
                        SignOutFromApple();
                        return;
                    }
                    
                    if (taskAuth.IsFaulted)
                    {
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
            var firebaseCredential = rgnCore.readyMasterAuth.oAuthProvider.GetCredential("apple.com", identityToken, rawNonce, authorizationCode);

            rgnCore.auth.SignInWithCredentialAsync(firebaseCredential).ContinueWithOnMainThread(task =>
            {
                Debug.LogFormat("[AppleSignInModule]: SignInWithCredentialAsync, isCanceled: {0}, isFaulted: {1}", task.IsCanceled, task.IsFaulted);

                if (task.IsCanceled)
                {
                    SignOutFromApple();
                    return;
                }
                
                if (task.IsFaulted)
                {
                    rgnCore.SetAuthCompletion(EnumLoginState.Error, EnumLoginError.Unknown);
                    return;
                }

                Debug.Log("[AppleSignInModule]: Sign In with Apple Successful." + task.Result.UserId);
                
                task.Result.TokenAsync(false).ContinueWithOnMainThread(taskToken =>
                {
                    if (taskToken.IsCanceled)
                    {
                        return;
                    }
                    
                    if (taskToken.IsFaulted)
                    {
                        rgnCore.SetAuthCompletion(EnumLoginState.Error, EnumLoginError.Unknown);
                        return;
                    }

                    Debug.Log("[AppleSignInModule]: Apple, userToken " + taskToken.Result);
                    
                    rgnCore.CreateCustomTokenAsync(taskToken.Result).ContinueWithOnMainThread(taskCustom =>
                    {
                        if (taskCustom.IsCanceled)
                        {
                            return;
                        }
                        
                        if (taskCustom.IsFaulted)
                        {
                            rgnCore.SetAuthCompletion(EnumLoginState.Error, EnumLoginError.Unknown);
                            return;
                        }
                        
                        if (string.IsNullOrEmpty(taskCustom.Result))
                        {
                            rgnCore.SetAuthCompletion(EnumLoginState.Error, EnumLoginError.Unknown);
                            return;
                        }

                        Debug.Log("[AppleSignInModule]: Apple, masterToken " + taskCustom.Result);
                        
                        rgnCore.readyMasterAuth.SignInWithCustomTokenAsync(taskCustom.Result);
                    });
                });
            });
        }
#endif

        public void SignOutFromApple()
        {
            rgnCore.SignOutRGN();
        }
    }
}