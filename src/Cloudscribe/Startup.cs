using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace cloudscribe_playground
{
    public class Startup
    {
        public Startup(
            IConfiguration configuration, 
            IHostingEnvironment env,
            ILogger<Startup> logger
            )
        {
            Configuration = configuration;
            Environment = env;
            _log = logger;

            SslIsAvailable = Configuration.GetValue<bool>("AppSettings:UseSsl");
            DisableIdentityServer = Configuration.GetValue<bool>("AppSettings:DisableIdentityServer");
        }

        private IConfiguration Configuration { get; set; }
        private IHostingEnvironment Environment { get; set; }
        private bool SslIsAvailable { get; set; }
        private bool DisableIdentityServer { get; set; }
        private bool didSetupIdServer = false;
        private ILogger _log;

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            // **** VERY IMPORTANT *****
            // https://www.cloudscribe.com/docs/configuring-data-protection
            // data protection keys are used to encrypt the auth token in the cookie
            // and also to encrypt social auth secrets and smtp password in the data storage
            // therefore we need keys to be persistent in order to be able to decrypt
            // if you move an app to different hosting and the keys change then you would have
            // to update those settings again from the Administration UI

            // for IIS hosting you should use a powershell script to create a keyring in the registry
            // per application pool and use a different application pool per app
            // https://docs.microsoft.com/en-us/aspnet/core/publishing/iis#data-protection
            // https://docs.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?tabs=aspnetcore2x
            if(Environment.IsProduction())
            {
                // If using Azure for production the uri with sas token could be stored in azure as environment variable or using key vault
                // but the keys go in azure blob storage per docs https://docs.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-storage-providers
                // this is false by default you should set it to true in azure environment variables
                var useBlobStroageForDataProtection = Configuration.GetValue<bool>("AppSettings:UseAzureBlobForDataProtection");
                // best to put this in azure environment variables instead of appsettings.json
                var storageConnectionString = Configuration["AppSettings:DataProtectionBlobStorageConnectionString"];
                if(useBlobStroageForDataProtection && !string.IsNullOrWhiteSpace(storageConnectionString))
                { 
                    var storageAccount =  Microsoft.WindowsAzure.Storage.CloudStorageAccount.Parse(storageConnectionString);
                    var client = storageAccount.CreateCloudBlobClient();
                    var container = client.GetContainerReference("key-container");
                    // The container must exist before calling the DataProtection APIs.
                    // The specific file within the container does not have to exist,
                    // as it will be created on-demand.
                    container.CreateIfNotExistsAsync().GetAwaiter().GetResult();
                    services.AddDataProtection()
                        .PersistKeysToAzureBlobStorage(container, "keys.xml");
 
                }
                else
                {
                    services.AddDataProtection();
                }
            }
            else
            {
                // dp_Keys folder should be added to .gitignore so the keys don't go into source control
                // ie add a line with: **/dp_keys/**
                // to your .gitignore file
                string pathToCryptoKeys = Path.Combine(Environment.ContentRootPath, "dp_keys");
                services.AddDataProtection()
                    .PersistKeysToFileSystem(new System.IO.DirectoryInfo(pathToCryptoKeys))
                    ;
            }

            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
            });

            services.AddMemoryCache();

            //services.AddSession();

            ConfigureAuthPolicy(services);

            services.AddOptions();


            services.AddCloudscribeCoreNoDbStorage();
            services.AddCloudscribeLoggingNoDbStorage(Configuration);
            services.AddCloudscribeLogging();
            services.AddNoDbStorageForSimpleContent();

            if (!DisableIdentityServer)
            {
                try 
                {
                    var idsBuilder = services.AddIdentityServerConfiguredForCloudscribe()
                        .AddCloudscribeCoreNoDbIdentityServerStorage()
                        .AddCloudscribeIdentityServerIntegrationMvc(); 
                    if(Environment.IsProduction())
                    {
                        // *** IMPORTANT CONFIGURATION NEEDED HERE *** 
                        // can't use .AddDeveloperSigningCredential in production it will throw an error
                        // https://identityserver4.readthedocs.io/en/dev/topics/crypto.html
                        // https://identityserver4.readthedocs.io/en/dev/topics/startup.html#refstartupkeymaterial
                        // you need to create an X.509 certificate (can be self signed)
                        // on your server and configure the cert file path and password name in appsettings.json
                        // OR change this code to wire up a certificate differently
                        _log.LogWarning("setting up identityserver4 for production");
                        var certPath = Configuration.GetValue<string>("AppSettings:IdServerSigningCertPath");
                        var certPwd = Configuration.GetValue<string>("AppSettings:IdServerSigningCertPassword");
                        if(!string.IsNullOrWhiteSpace(certPath) && !string.IsNullOrWhiteSpace(certPwd))
                        {
                            var cert = new X509Certificate2(
                            File.ReadAllBytes(certPath),
                            certPwd,
                            X509KeyStorageFlags.MachineKeySet |
                            X509KeyStorageFlags.PersistKeySet |
                            X509KeyStorageFlags.Exportable);

                            idsBuilder.AddSigningCredential(cert);
                            didSetupIdServer = true;
                        } 

                    }
                    else
                    {
                        idsBuilder.AddDeveloperSigningCredential(); // don't use this for production
                        didSetupIdServer = true;
                    }

                }
                catch(Exception ex)
                {
                    _log.LogError($"failed to setup identityserver4 {ex.Message} {ex.StackTrace}");
                }

                
 
            }
            
            services.AddCors(options =>
            {
                // this defines a CORS policy called "default"
                // add your IdentityServer client apps and apis to allow access to them
                options.AddPolicy("default", policy =>
                {
                    policy.AllowAnyOrigin()
                        .AllowAnyHeader()
                        .AllowAnyMethod();
                });
            });

            services.AddScoped<cloudscribe.Web.Navigation.INavigationNodePermissionResolver, cloudscribe.Web.Navigation.NavigationNodePermissionResolver>();
            services.AddScoped<cloudscribe.Web.Navigation.INavigationNodePermissionResolver, cloudscribe.SimpleContent.Web.Services.PagesNavigationNodePermissionResolver>();
            services.AddCloudscribeCoreMvc(Configuration);
            services.AddCloudscribeCoreIntegrationForSimpleContent(Configuration);
            services.AddSimpleContentMvc(Configuration);
            services.AddMetaWeblogForSimpleContent(Configuration.GetSection("MetaWeblogApiOptions"));
            services.AddSimpleContentRssSyndiction();
            
            // optional but recommended if you need localization 
            // uncomment to use cloudscribe.Web.localization https://github.com/joeaudette/cloudscribe.Web.Localization
            //services.Configure<GlobalResourceOptions>(Configuration.GetSection("GlobalResourceOptions"));
            //services.AddSingleton<IStringLocalizerFactory, GlobalResourceManagerStringLocalizerFactory>();

            services.AddLocalization(options => options.ResourcesPath = "GlobalResources");

            services.Configure<RequestLocalizationOptions>(options =>
            {
                var supportedCultures = new[]
                {
                    new CultureInfo("en-US"),
                    //new CultureInfo("en-GB"),
                    //new CultureInfo("fr-FR"),
                    //new CultureInfo("fr"),
                };

                // State what the default culture for your application is. This will be used if no specific culture
                // can be determined for a given request.
                options.DefaultRequestCulture = new RequestCulture(culture: "en-US", uiCulture: "en-US");

                // You must explicitly state which cultures your application supports.
                // These are the cultures the app supports for formatting numbers, dates, etc.
                options.SupportedCultures = supportedCultures;

                // These are the cultures the app supports for UI strings, i.e. we have localized resources for.
                options.SupportedUICultures = supportedCultures;

                // You can change which providers are configured to determine the culture for requests, or even add a custom
                // provider with your own logic. The providers will be asked in order to provide a culture for each request,
                // and the first to provide a non-null result that is in the configured supported cultures list will be used.
                // By default, the following built-in providers are configured:
                // - QueryStringRequestCultureProvider, sets culture via "culture" and "ui-culture" query string values, useful for testing
                // - CookieRequestCultureProvider, sets culture via "ASPNET_CULTURE" cookie
                // - AcceptLanguageHeaderRequestCultureProvider, sets culture via the "Accept-Language" request header
                //options.RequestCultureProviders.Insert(0, new CustomRequestCultureProvider(async context =>
                //{
                //  // My custom request culture logic
                //  return new ProviderCultureResult("en");
                //}));
            });

            services.Configure<MvcOptions>(options =>
            {
                if (SslIsAvailable)
                {
                    options.Filters.Add(new RequireHttpsAttribute());
                }

                options.CacheProfiles.Add("SiteMapCacheProfile",
                     new CacheProfile
                     {
                         Duration = 30
                     });

                options.CacheProfiles.Add("RssCacheProfile",
                     new CacheProfile
                     {
                         Duration = 100
                     });
            });

            services.AddRouting(options =>
            {
                options.LowercaseUrls = true;
            });

            services.AddMvc()
                .AddRazorOptions(options =>
                {
                    options.AddCloudscribeCommonEmbeddedViews();
                    options.AddCloudscribeNavigationBootstrap3Views();
                    options.AddCloudscribeCoreBootstrap3Views();
                    options.AddCloudscribeSimpleContentBootstrap3Views();
                    options.AddCloudscribeFileManagerBootstrap3Views();
                    options.AddCloudscribeLoggingBootstrap3Views();
                    options.AddCloudscribeCoreIdentityServerIntegrationBootstrap3Views();

                    options.ViewLocationExpanders.Add(new cloudscribe.Core.Web.Components.SiteViewLocationExpander());
                });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(
            IApplicationBuilder app, 
            IHostingEnvironment env,
            ILoggerFactory loggerFactory,
            IOptions<cloudscribe.Core.Models.MultiTenantOptions> multiTenantOptionsAccessor,
            IOptions<RequestLocalizationOptions> localizationOptionsAccessor
            )
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseBrowserLink();
                app.UseDatabaseErrorPage();
            }
            else
            {
                app.UseExceptionHandler("/oops/error");
            }

            app.UseForwardedHeaders();
            app.UseStaticFiles();

            //app.UseSession();

            app.UseRequestLocalization(localizationOptionsAccessor.Value);
            app.UseCors("default"); //use Cors with policy named default, defined above

            var multiTenantOptions = multiTenantOptionsAccessor.Value;

            app.UseCloudscribeCore(
                    loggerFactory,
                    multiTenantOptions,
                    SslIsAvailable);

            if (!DisableIdentityServer && didSetupIdServer)
            {
                try
                {
                    app.UseIdentityServer();
                }
                catch(Exception ex)
                {
                    _log.LogError($"failed to setup identityserver4 {ex.Message} {ex.StackTrace}");
                }
            }
            UseMvc(app, multiTenantOptions.Mode == cloudscribe.Core.Models.MultiTenantMode.FolderName);
            
        }
        private void UseMvc(IApplicationBuilder app, bool useFolders)
        {
            app.UseMvc(routes =>
            {
                if (useFolders)
                {
                    routes.AddBlogRoutesForSimpleContent(new cloudscribe.Core.Web.Components.SiteFolderRouteConstraint());
                }
                
                routes.AddBlogRoutesForSimpleContent();
                routes.AddSimpleContentStaticResourceRoutes();
                routes.AddCloudscribeFileManagerRoutes();
                if (useFolders)
                {
                    routes.MapRoute(
                       name: "foldererrorhandler",
                       template: "{sitefolder}/oops/error/{statusCode?}",
                       defaults: new { controller = "Oops", action = "Error" },
                       constraints: new { name = new cloudscribe.Core.Web.Components.SiteFolderRouteConstraint() }
                    );


                    
                    routes.MapRoute(
                            name: "foldersitemap",
                            template: "{sitefolder}/sitemap"
                            , defaults: new { controller = "Page", action = "SiteMap" }
                            , constraints: new { name = new cloudscribe.Core.Web.Components.SiteFolderRouteConstraint() }
                            );
                    routes.MapRoute(
                        name: "folderdefault",
                        template: "{sitefolder}/{controller}/{action}/{id?}",
                        defaults: null,
                        constraints: new { name = new cloudscribe.Core.Web.Components.SiteFolderRouteConstraint() }
                        );
                    routes.AddDefaultPageRouteForSimpleContent(new cloudscribe.Core.Web.Components.SiteFolderRouteConstraint());


                }

                routes.MapRoute(
                    name: "errorhandler",
                    template: "oops/error/{statusCode?}",
                    defaults: new { controller = "Oops", action = "Error" }
                    );


                routes.MapRoute(
                    name: "sitemap",
                    template: "sitemap"
                    , defaults: new { controller = "Page", action = "SiteMap" }
                    );
                routes.MapRoute(
                    name: "def",
                    template: "{controller}/{action}"
                    //,defaults: new { controller = "Home", action = "Index" }
                    );
                routes.AddDefaultPageRouteForSimpleContent();
                



            });
        }

        private void ConfigureAuthPolicy(IServiceCollection services)
        {
            //https://docs.asp.net/en/latest/security/authorization/policies.html

            services.AddAuthorization(options =>
            {
                options.AddCloudscribeCoreDefaultPolicies();
                options.AddCloudscribeLoggingDefaultPolicy();
                
                options.AddCloudscribeCoreSimpleContentIntegrationDefaultPolicies();
                // this is what the above extension adds
                //options.AddPolicy(
                //    "BlogEditPolicy",
                //    authBuilder =>
                //    {
                //        //authBuilder.RequireClaim("blogId");
                //        authBuilder.RequireRole("Administrators");
                //    }
                // );

                //options.AddPolicy(
                //    "PageEditPolicy",
                //    authBuilder =>
                //    {
                //        authBuilder.RequireRole("Administrators");
                //    });

                options.AddPolicy(
                    "FileManagerPolicy",
                    authBuilder =>
                    {
                        authBuilder.RequireRole("Administrators", "Content Administrators");
                    });

                options.AddPolicy(
                    "FileManagerDeletePolicy",
                    authBuilder =>
                    {
                        authBuilder.RequireRole("Administrators", "Content Administrators");
                    });

                options.AddPolicy(
                    "IdentityServerAdminPolicy",
                    authBuilder =>
                    {
                        authBuilder.RequireRole("Administrators");
                    });

                // add other policies here 

            });

        }

        
    }
}