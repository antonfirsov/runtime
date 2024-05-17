// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.DotNet.RemoteExecutor;
using Xunit;

namespace System.Net.NameResolution.Tests
{
    public class ActivityTest
    {
        private class ActivityRecorder : IDisposable
        {
            private const string ActivitySourceName = "System.Net.NameResolution";
            private const string ActivityName = ActivitySourceName + ".DsnLookup";

            private readonly ActivityListener _listener;
            private int _started;
            private int _stopped;

            public Activity ExpectedParent { get; set; }

            public ActivityRecorder()
            {
                _listener = new ActivityListener
                {
                    ShouldListenTo = (activitySource) => activitySource.Name == ActivitySourceName,
                    Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
                    ActivityStarted = (activity) => {
                        if (activity.OperationName == ActivityName)
                        {
                            Assert.Same(ExpectedParent, activity.Parent);
                            _started++;
                        }
                    },
                    ActivityStopped = (activity) => {
                        if (activity.OperationName == ActivityName)
                        {
                            Assert.Same(ExpectedParent, activity.Parent);
                            _stopped++;
                        }
                    }
                };

                ActivitySource.AddActivityListener(_listener);
            }

            public void Dispose() => _listener.Dispose();

            public void VerifyActivityRecorded(int times)
            {
                Assert.Equal(times, _started);
                Assert.Equal(times, _stopped);
            }
        }

        [ConditionalTheory(typeof(RemoteExecutor), nameof(RemoteExecutor.IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public void ResolveValidHostName_ActivityRecorded(bool createParentActivity)
        {
            RemoteExecutor.Invoke(static async (createParentActivity) =>
            {
                const string ValidHostName = "localhost";
                using var recorder = new ActivityRecorder()
                {
                    ExpectedParent = bool.Parse(createParentActivity) ? new Activity("parent").Start() : null
                };
                
                await Dns.GetHostEntryAsync(ValidHostName);
                recorder.VerifyActivityRecorded(times: 1);

                await Dns.GetHostAddressesAsync(ValidHostName);
                recorder.VerifyActivityRecorded(times: 2);

                Dns.GetHostEntry(ValidHostName);
                recorder.VerifyActivityRecorded(times: 3);

                Dns.GetHostAddresses(ValidHostName);
                recorder.VerifyActivityRecorded(times: 4);

                Dns.EndGetHostEntry(Dns.BeginGetHostEntry(ValidHostName, null, null));
                recorder.VerifyActivityRecorded(times: 5);

                Dns.EndGetHostAddresses(Dns.BeginGetHostAddresses(ValidHostName, null, null));
                recorder.VerifyActivityRecorded(times: 6);

            }, createParentActivity.ToString()).Dispose();
        }

        [ConditionalTheory(typeof(RemoteExecutor), nameof(RemoteExecutor.IsSupported))]
        [InlineData(false)]
        [InlineData(true)]
        public static void ResolveInvalidHostName_ActivityRecorded(bool createParentActivity)
        {
            const string InvalidHostName = $"invalid...example.com...{nameof(ResolveInvalidHostName_ActivityRecorded)}";

            RemoteExecutor.Invoke(async (createParentActivity) =>
            {
                using var recorder = new ActivityRecorder()
                {
                    ExpectedParent = bool.Parse(createParentActivity) ? new Activity("parent").Start() : null
                };

                await Assert.ThrowsAnyAsync<SocketException>(async () => await Dns.GetHostEntryAsync(InvalidHostName));
                await Assert.ThrowsAnyAsync<SocketException>(async () => await Dns.GetHostAddressesAsync(InvalidHostName));

                Assert.ThrowsAny<SocketException>(() => Dns.GetHostEntry(InvalidHostName));
                Assert.ThrowsAny<SocketException>(() => Dns.GetHostAddresses(InvalidHostName));

                Assert.ThrowsAny<SocketException>(() => Dns.EndGetHostEntry(Dns.BeginGetHostEntry(InvalidHostName, null, null)));
                Assert.ThrowsAny<SocketException>(() => Dns.EndGetHostAddresses(Dns.BeginGetHostAddresses(InvalidHostName, null, null)));

                recorder.VerifyActivityRecorded(times: 6);
            }, createParentActivity.ToString()).Dispose();
        }
    }
}
