﻿using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.Serialization;
using Marvin.Products.Model;

namespace Marvin.Products.Management
{
    /// <summary>
    /// Configuration for <see cref="GenericLinkStrategy"/>
    /// </summary>
    [DataContract]
    public class GenericLinkConfiguration : ProductLinkConfiguration, IGenericMapperConfiguration
    {
        public override string PluginName
        {
            get { return nameof(GenericLinkStrategy); }
            set { }
        }

        /// <inheritdoc />
        [DataMember, DefaultValue(nameof(IGenericColumns.Text8)), AvailableColumns(typeof(string))]
        [Description("Column that should be used to store all non-configured properties as JSON")]
        public string JsonColumn { get; set; }

        /// <inheritdoc />
        [DataMember, Description]
        public List<PropertyMapperConfig> PropertyConfigs { get; set; }
    }
}