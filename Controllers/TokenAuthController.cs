using System;
//using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Microsoft.AspNetCore.Mvc;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Principal;
using System.Collections.Generic;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.WindowsAzure.Storage; // Namespace for Storage Client Library
using Microsoft.WindowsAzure.Storage.File; // Namespace for Azure File storage
using PlayList.Models;
using System.Threading.Tasks;
using System.IO;
using PlayList.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.WindowsAzure.Storage.Blob;

namespace PlayList
{
    [Produces("application/json")]
    [Route("api/tokenauth")]
    public class TokenAuthController : Controller
    {
        private readonly IMultiSourcePlaylistRepository _multiSourcePlaylistRepository;
        private readonly ILogger _logger;
        private readonly IHostingEnvironment _environment;
        private IConfiguration _configuration { get; }
        private int azureFileCount;
        private long azureUsage;
        private int? azureQuota;
        public TokenAuthController(
            IHostingEnvironment environment,
            IMultiSourcePlaylistRepository multiSourcePlaylistRepository,
            ILoggerFactory loggerFactory,
            IConfiguration configuration)
        :base()
        {
            _environment = environment;
            _multiSourcePlaylistRepository = multiSourcePlaylistRepository;
            _logger = loggerFactory.CreateLogger("TokenAuthController");  
            _configuration = configuration;
        }
        [HttpPut("Login/{rememberme}")]
        [AllowAnonymous]
        public async Task<IActionResult> Login(bool rememberme, [FromBody]User user)
        {
            PasswordHasher<User> hasher = new PasswordHasher<User>();
            User existUser = _multiSourcePlaylistRepository
                .GetAllUsers()
                .FirstOrDefault(u => 
                    u.Username == user.Username
                    && hasher.VerifyHashedPassword(user,u.Password, user.Password) == PasswordVerificationResult.Success );

            if (existUser != null)
            {

                var requestAt = DateTime.Now;
                var expiresIn = requestAt + TokenAuthOption.GetExpiresSpan(rememberme);
                var token = await GenerateToken(existUser, expiresIn, rememberme);

                return Json(new RequestResult
                {
                    State = RequestState.Success,
                    Data = new
                    {
                        requertAt = requestAt,
                        expiresIn = TokenAuthOption.GetExpiresSpan(rememberme).TotalSeconds,
                        tokeyType = TokenAuthOption.TokenType,
                        accessToken = token
                    }
                });
            }
            else
            {
                return Json(new RequestResult
                {
                    State = RequestState.Failed,
                    Msg = "Username or password is invalid"
                });
            }
        }
        [HttpPost("Register/{rememberme}")]
        [AllowAnonymous]
        public async Task<IActionResult> Register(bool rememberme, [FromBody]User user)
        {
            User existUser = _multiSourcePlaylistRepository.GetAllUsers().FirstOrDefault(u => u.Username == user.Username);

            if (existUser == null)
            {

                PasswordHasher<User> hasher = new PasswordHasher<User>();
                string hashedPW = hasher.HashPassword(user,user.Password);
                var userfileFolder = Guid.NewGuid().ToString();
                var uploads = Path.Combine(_environment.ContentRootPath,
                "uploads", userfileFolder);
                Directory.CreateDirectory(uploads);
                CloudStorageAccount storageAccount = CloudStorageAccount.Parse(
                    _configuration["Production:StorageConnectionString"]);
                CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
                CloudBlobContainer container = blobClient.GetContainerReference(userfileFolder);
                await container.CreateIfNotExistsAsync();
                // Ensure that the share exists.
                if (!await container.ExistsAsync())
                {
                    return Json(new RequestResult
                    {
                        State = RequestState.Failed,
                        Msg = "Cannot create share to cloud."
                    });
                }
                user.Password = hashedPW;
                user.FileFolder = userfileFolder;
                _logger.LogCritical(JsonConvert.SerializeObject(user));
                _multiSourcePlaylistRepository.PostUser(user);

                User newUser = _multiSourcePlaylistRepository.GetAllUsers().FirstOrDefault(u => u.Username == user.Username);
                
                if(newUser != null)
                {
                    var requestAt = DateTime.Now;
                    var expiresIn = requestAt + TokenAuthOption.GetExpiresSpan(rememberme);
                    var token = await GenerateToken(newUser, expiresIn, rememberme);

                    return Json(new RequestResult
                    {
                        State = RequestState.Success,
                        Data = new
                        {
                            requertAt = requestAt,
                            expiresIn = TokenAuthOption.GetExpiresSpan(rememberme).TotalSeconds,
                            tokeyType = TokenAuthOption.TokenType,
                            accessToken = token
                        }
                    });
                }
                else
                {
                    return Json(new RequestResult
                    {
                        State = RequestState.Failed,
                        Msg = "Adding new user failed."
                    });
                }
            }
            else
            {
                return Json(new RequestResult
                {
                    State = RequestState.Failed,
                    Msg = "Username is already in use."
                });
            }
        }

        private async Task<IActionResult> GenerateToken(PlayList.Models.User user, DateTime expires, bool rememberme)
        {
            var handler = new JwtSecurityTokenHandler();
            ClaimsIdentity identity = new ClaimsIdentity(
                new GenericIdentity(user.Username, "TokenAuth"),
                new[] { new Claim("Id", user.Id.ToString())}
            );
            AuthenticationProperties authProperties = null;
            if (rememberme)
            {
                authProperties = new AuthenticationProperties
                {
                    IsPersistent = true
                };
            }
            else
            {
                authProperties = new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = expires
                };
            }
            // Clear the existing external cookie to ensure a clean login process
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                authProperties);

            var securityToken = handler.CreateToken(new SecurityTokenDescriptor
            {
                Issuer = TokenAuthOption.Issuer,
                Audience = TokenAuthOption.Audience,
                SigningCredentials = TokenAuthOption.SigningCredentials,
                Subject = identity,
                Expires = expires
            });
            return Ok(handler.WriteToken(securityToken));
        }

        [HttpGet]
        [Authorize("Bearer")]
        public async Task<IActionResult> GetUserInfo()
        {
            var claimsIdentity = User.Identity as ClaimsIdentity;
            var Id = Convert.ToInt64(claimsIdentity.Claims.FirstOrDefault(claim => claim.Type == "Id").Value);
            var user = _multiSourcePlaylistRepository.GetUser(Id);
            
            var youTubeTrackCount = _multiSourcePlaylistRepository.GetUsersTrackCountByType(Id,1);
            var spotifyTrackCount = _multiSourcePlaylistRepository.GetUsersTrackCountByType(Id,2);
            var mp3TrackCount = _multiSourcePlaylistRepository.GetUsersTrackCountByType(Id,3) +
                                _multiSourcePlaylistRepository.GetUsersTrackCountByType(Id,5);
            var trackCount = _multiSourcePlaylistRepository.GetUsersTrackCount(Id);
            var playlistCount = _multiSourcePlaylistRepository.GetUsersPlaylistCount(Id);
            var temp = await getAzureInfo(user.FileFolder);
            var mp3fileAmount = azureFileCount;
            var sizeInBytes = azureUsage;
            var sizeInMb = sizeInBytes / 1048576;//1024*1024
            var quota = azureQuota;
            var usersMaxDirectorySize = quota * 1000;
            var SASToken = GetSASToken(user);
            return Json(new RequestResult
            {
                State = RequestState.Success,
                Data = new { Id = Id,
                            UserName = claimsIdentity.Name,
                            IsAuthenticated = claimsIdentity.IsAuthenticated,
                            MaxDiscSpace = usersMaxDirectorySize,
                            UsedDiscSpace = sizeInMb,
                            FileAmount = mp3fileAmount,
                            TrackCount = trackCount,
                            SpotifyTrackCount = spotifyTrackCount,
                            YoutubeTrackCount = youTubeTrackCount,
                            Mp3TrackCount = mp3TrackCount,
                            PlaylistCount = playlistCount,
                            FirstName = user.Fname,
                            LastName = user.Lname,
                            Folder = user.FileFolder,
                            SASToken = SASToken }
            });
        }

        private async Task<string> getAzureInfo(string filefolder)
        {

            azureQuota = 10;
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(
                    _configuration["Production:StorageConnectionString"]);
            CloudFileClient fileClient = storageAccount.CreateCloudFileClient();
            // Get a reference to the file share we created previously.
            CloudFileShare share = fileClient.GetShareReference(filefolder);
            CloudFileDirectory userDir = null;
            int fileCountInFileStore = 0;
            long totalFileSizeInFileStore = 0;
            // Ensure that the share exists.
            if (await share.ExistsAsync())
            {
                // Get a reference to the root directory for the share.
                CloudFileDirectory rootDir = share.GetRootDirectoryReference();
                // Get a reference to the directory we created previously.
                userDir = rootDir.GetDirectoryReference("audio");
                // Ensure that the directory exists.
                if (await userDir.ExistsAsync())
                {
                    var results = new List<IListFileItem>();
                    FileContinuationToken token = null;
                    try
                    {
                        do
                        {
                            FileResultSegment resultSegment = await userDir.ListFilesAndDirectoriesSegmentedAsync(token);
                            results.AddRange(resultSegment.Results);
                            token = resultSegment.ContinuationToken;
                        }
                        while (token != null);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                        Console.ReadKey();
                    }
                    fileCountInFileStore = results.Count;
                    long sizeInBytes = 0;
                    results.ForEach( x=>
                    {
                        if (x is CloudFile)
                        {
                            var cloudFile = (CloudFile) x;
                            sizeInBytes += cloudFile.Properties.Length;
                        }
                    });
                    totalFileSizeInFileStore = sizeInBytes;
                }
            }

            CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
            CloudBlobContainer container = blobClient.GetContainerReference(filefolder);
            // Ensure that the container exists.
            if (await container.ExistsAsync())
            {
                var blobresults = new List<IListBlobItem>();
                try
                {
                    BlobContinuationToken blobContinuationToken = null;
                    do
                    {
                        BlobResultSegment resultSegment = await container.ListBlobsSegmentedAsync(null, blobContinuationToken);
                        // Get the value of the continuation token returned by the listing call.
                        blobresults.AddRange(resultSegment.Results);
                        blobContinuationToken = resultSegment.ContinuationToken;
                    } while (blobContinuationToken != null); // Loop while the continuation token is not null.
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    Console.ReadKey();
                }
                azureFileCount = fileCountInFileStore + blobresults.Count;
                long sizeInBytes = 0;
                blobresults.ForEach( x=>
                {
                    if (x is CloudBlob)
                    {
                        var cloudBlob = (CloudBlob) x;
                        sizeInBytes += cloudBlob.Properties.Length;
                    }
                });
                azureUsage = totalFileSizeInFileStore + sizeInBytes;
                
            }
            return "SUCCESS";
        }
        private string GetSASToken(User user) {
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(
                _configuration["Production:StorageConnectionString"]);
            
            CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
            CloudBlobContainer container = blobClient.GetContainerReference(user.FileFolder);
            return container.GetSharedAccessSignature(new SharedAccessBlobPolicy() {
                Permissions = SharedAccessBlobPermissions.Read,
                SharedAccessExpiryTime = new DateTimeOffset(DateTime.UtcNow.AddDays(1)),
                SharedAccessStartTime = new DateTimeOffset(DateTime.UtcNow)
            });
            
        }
    }
}
