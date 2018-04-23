﻿using Marvin.Model;
using Marvin.Products.Model;

namespace Marvin.Products.Samples.Model
{
    /// <summary>
    /// Factory to get a unit of work for the resources model
    /// </summary>
    [ModelFactory(WatchProductsConstants.Namespace, ProductsConstants.Namespace)]
    public class WatchProductsUnitOfWorkFactory : ProductsUnitOfWorkFactory<WatchProductsContext>
    {
        /// <inheritdoc />
        protected override void Configure()
        {
            base.Configure();

            RegisterRepository<ISmartWatchProductPropertiesEntityRepository>();
            RegisterRepository<IAnalogWatchProductPropertiesEntityRepository>();
        }
    }
}