﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using NuGet.Protocol.Core.Types;
using Test.Utility;
using Xunit;


namespace NuGet.Protocol.VisualStudio.Tests
{
    //Tests the Powershell autocomplete resource for V2 and v3 sources.  
    public class PowershellAutoCompleteResourceTests
    {
         private static Dictionary<string,string> ResponsesDict;
         public PowershellAutoCompleteResourceTests()
         {
            ResponsesDict = new Dictionary<string, string>();
            ResponsesDict.Add("http://testsource.com/v3/index.json", JsonData.IndexJson);
            ResponsesDict.Add("https://api-v3search-0.nuget.org/autocomplete?q=elm", JsonData.PsAutoCompleteV3Example);
            ResponsesDict.Add("https://nuget.org/api/v2/package-ids?partialId=elm", JsonData.PSAutoCompleteV2Example);
         }
        
        [Theory]
        [InlineData("http://testsource.com/v3/index.json", "v3Source")]
        [InlineData("https://nuget.org/api/v2/", "v2source")]
        public async Task PowershellAutoComplete_IdStartsWith(string sourceUrl,string sourceName)
        { 
            var source = StaticHttpHandler.CreateSource(sourceUrl, Repository.Provider.GetVisualStudio(), ResponsesDict);
            //var source =  new SourceRepository(new Configuration.PackageSource(sourceUrl,sourceName), Repository.Provider.GetVisualStudio());            
            var resource = await source.GetResourceAsync<PSAutoCompleteResource>();
            Assert.NotNull(resource);
            CancellationTokenSource cancellationToken = new CancellationTokenSource();
            IEnumerable<string> packages = await resource.IdStartsWith("elm", true, cancellationToken.Token);
            Assert.True(packages != null & packages.Count() > 0);
            Assert.Contains("elmah", packages);
        }

        [Theory]
        [InlineData("http://testsource.com/v3/index.json", "v3Source")]
        [InlineData("https://nuget.org/api/v2/", "v2source")]
        public async Task PowershellAutoComplete_Cancel(string sourceUrl, string sourceName)
        {
            var source = StaticHttpHandler.CreateSource(sourceUrl, Repository.Provider.GetVisualStudio(), ResponsesDict);
            var resource = await source.GetResourceAsync<PSAutoCompleteResource>();
            Assert.NotNull(resource);
            CancellationTokenSource cancellationToken = new CancellationTokenSource();
            Task<IEnumerable<string>> packagesTask = resource.IdStartsWith("elm", true, cancellationToken.Token);
            cancellationToken.Cancel();
            //Assert.Throw(Exception) doesn't seem to be working as expected. Hence using regular try and catch and asserting in catch block.
            try
            {
                packagesTask.Wait();                   
            }catch(AggregateException e)
            {
            Assert.True(e.InnerExceptions.Count() == 1);
            Assert.True(e.InnerExceptions.Any(item => item.GetType().Equals(typeof(TaskCanceledException))));
            }            
        }
    }
}