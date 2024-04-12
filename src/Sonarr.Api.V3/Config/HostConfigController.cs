using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Update;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using Sonarr.Http;
using Sonarr.Http.REST;
using Sonarr.Http.REST.Attributes;

namespace Sonarr.Api.V3.Config
{
    [V3ApiController("config/host")]
    public class HostConfigController : RestController<HostConfigResource>
    {
        private readonly IConfigFileProvider _configFileProvider;
        private readonly IConfigService _configService;
        private readonly IUserService _userService;

        public HostConfigController(IConfigFileProvider configFileProvider,
                                    IConfigService configService,
                                    IUserService userService,
                                    FileExistsValidator fileExistsValidator)
        {
            _configFileProvider = configFileProvider;
            _configService = configService;
            _userService = userService;

            SharedValidator.RuleFor(c => c.BindAddress)
                           .ValidIpAddress()
                           .NotListenAllIp4Address()
                           .When(c => c.BindAddress != "*" && c.BindAddress != "localhost");

            SharedValidator.RuleFor(c => c.Port).ValidPort();

            SharedValidator.RuleFor(c => c.UrlBase).ValidUrlBase();
            SharedValidator.RuleFor(c => c.InstanceName).StartsOrEndsWithSonarr();

            // Username and password should not be empty for basic and forms authentication
            SharedValidator.RuleForEach(c => c.Users)
                .ChildRules(user =>
                {
                    user.RuleFor(u => u.Username).NotEmpty();
                    user.RuleFor(u => u.Password).NotEmpty();
                })
                .When(c => c.AuthenticationMethod == AuthenticationType.Basic ||
                        c.AuthenticationMethod == AuthenticationType.Forms);

            // Password should match password confirmation (or existing password if it exists)
            SharedValidator.RuleForEach(c => c.Users)
                .ChildRules(user =>
                    user.RuleFor(c => c.PasswordConfirmation)
                        .Must((resource, p) => IsMatchingPassword(resource)).WithMessage("Must match Password"));

            // Usernames must be unique and there must be at least one user
            SharedValidator.RuleFor(c => c.Users)
                .NotEmpty()
                .Must((resource, users) =>
                    users.Select(u => u.Username).Distinct().Count() == users.Count)
                .WithMessage("Usernames must be unique");

            SharedValidator.RuleFor(c => c.SslPort).ValidPort().When(c => c.EnableSsl);
            SharedValidator.RuleFor(c => c.SslPort).NotEqual(c => c.Port).When(c => c.EnableSsl);

            SharedValidator.RuleFor(c => c.SslCertPath)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .IsValidPath()
                .SetValidator(fileExistsValidator)
                .Must((resource, path) => IsValidSslCertificate(resource)).WithMessage("Invalid SSL certificate file or password")
                .When(c => c.EnableSsl);

            SharedValidator.RuleFor(c => c.Branch).NotEmpty().WithMessage("Branch name is required, 'main' is the default");
            SharedValidator.RuleFor(c => c.UpdateScriptPath).IsValidPath().When(c => c.UpdateMechanism == UpdateMechanism.Script);

            SharedValidator.RuleFor(c => c.BackupFolder).IsValidPath().When(c => Path.IsPathRooted(c.BackupFolder));
            SharedValidator.RuleFor(c => c.BackupInterval).InclusiveBetween(1, 7);
            SharedValidator.RuleFor(c => c.BackupRetention).InclusiveBetween(1, 90);
        }

        private bool IsValidSslCertificate(HostConfigResource resource)
        {
            X509Certificate2 cert;
            try
            {
                cert = new X509Certificate2(resource.SslCertPath, resource.SslCertPassword, X509KeyStorageFlags.DefaultKeySet);
            }
            catch
            {
                return false;
            }

            return cert != null;
        }

        /// <summary>
        /// Ensure either the password matches the stored password or the password matches the password confirmation.
        /// </summary>
        /// <param name="configUser"></param>
        /// <returns></returns>
        private bool IsMatchingPassword(HostConfigUser configUser)
        {
            var user = _userService.FindUser(configUser.Identifier);

            if (user != null && user.Password == configUser.Password)
            {
                return true;
            }

            if (configUser.Password == configUser.PasswordConfirmation)
            {
                return true;
            }

            return false;
        }

        protected override HostConfigResource GetResourceById(int id)
        {
            return GetHostConfig();
        }

        [HttpGet]
        public HostConfigResource GetHostConfig()
        {
            var resource = _configFileProvider.ToResource(_configService);
            resource.Id = 1;

            var users = _userService.All() ?? new List<User>();
            if (users.Count == 0)
            {
                users.Add(new User
                {
                    Identifier = Guid.NewGuid(),
                    Username = "admin"
                });
                users.Add(new User
                {
                    Identifier = Guid.NewGuid(),
                    Username = "user"
                });
            }

            resource.Users = users
                .Select(u => new HostConfigUser
                {
                    Identifier = u.Identifier,
                    Username = u.Username,
                    Password = u.Password,
                    PasswordConfirmation = string.Empty
                })
                .ToList();

            return resource;
        }

        [RestPutById]
        public ActionResult<HostConfigResource> SaveHostConfig(HostConfigResource resource)
        {
            var dictionary = resource.GetType()
                                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                                     .ToDictionary(prop => prop.Name, prop => prop.GetValue(resource, null));

            _configFileProvider.SaveConfigDictionary(dictionary);
            _configService.SaveConfigDictionary(dictionary);

            // TODO FIXME - handle add/remove/chage properly
            // if (resource.Username.IsNotNullOrWhiteSpace() && resource.Password.IsNotNullOrWhiteSpace())
            // {
            //     _userService.Upsert(resource.Username, resource.Password);
            // }

            return Accepted(resource.Id);
        }
    }
}
