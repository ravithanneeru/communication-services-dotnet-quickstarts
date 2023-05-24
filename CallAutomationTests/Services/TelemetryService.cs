﻿using CallAutomation.Scenarios.Interfaces;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;

namespace CallAutomation.Scenarios.Services
{
    public class TelemetryService : ITelemetryService
    {

        private readonly TelemetryClient _telemetryClient;

        public TelemetryService(TelemetryClient telemetryClient)
        {
            _telemetryClient = telemetryClient;
        }



        public bool TrackEvent(string eventName, Dictionary<string, string> properties)
        {
            _telemetryClient.TrackEvent(eventName, properties);
            return true;
        }

        public bool TrackMetric(string metricName, double value )
        {
            MetricTelemetry metric = new MetricTelemetry();
            metric.Name = metricName;
            metric.Sum = value;
            metric.Timestamp = DateTime.UtcNow;
            _telemetryClient.TrackMetric(metric);
            return true;
        }
    }
}