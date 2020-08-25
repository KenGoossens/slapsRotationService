using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;


namespace slapsWinService
{
    class AzureFunctionsBody
    {
        public string KeyName { get; set; }
        public string ContentType  { get; set; }
        public Dictionary<string,string> Tags { get; set; }
    }
    class JsonManipulations
    {
        public string Serialize(AzureFunctionsBody value)
        {
            return JsonSerializer.Serialize<AzureFunctionsBody>(value);
        }
    }
}
