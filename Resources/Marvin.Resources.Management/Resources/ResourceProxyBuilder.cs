﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Marvin.AbstractionLayer.Resources;

namespace Marvin.Resources.Management
{
    /// <summary>
    /// Class that can construct a new <see cref="ResourceProxy{TTarget}"/> for a given
    /// resource type
    /// </summary>
    internal static class ResourceProxyBuilder
    {
        /// <summary>
        /// Static module builder reference
        /// </summary>
        public static ModuleBuilder ModuleBuilder;

        /// <summary>
        /// Method attributes for properties and events
        /// </summary>
        private const MethodAttributes SpecialNameAttributes = MethodAttributes.Public | MethodAttributes.SpecialName
        | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot;

        /// <summary>
        /// Build a proxy for this resouce type
        /// </summary>
        public static Type Build(Type resourceType, Type[] interfaces)
        {
            // Create a type based on ResourceProxy and implement all interfaces
            var baseType = typeof(ResourceProxy<>).MakeGenericType(resourceType);
            var typeBuilder = ModuleBuilder.DefineType($"{resourceType.Name}Proxy", TypeAttributes.Public, baseType);

            // Define a constructor
            DefineConstructor(typeBuilder, baseType, resourceType);

            // Define the interfaces
            foreach (var providedInterface in interfaces)
            {
                typeBuilder.AddInterfaceImplementation(providedInterface);
            }

            // Target field for property and method forwarding
            const string propertyName = nameof(ResourceProxy<PublicResource>.Target);
            var targetProperty = baseType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic);

            var bindingFlags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public;
            // Define the properties
            var properties = interfaces.SelectMany(inter => inter.GetProperties(bindingFlags)).GroupBy(p => p.Name);
            foreach (var propertyGroup in properties)
            {
                DefineProperty(typeBuilder, baseType, targetProperty.GetMethod, resourceType, propertyGroup);
            }

            // Define methods
            var methods = interfaces.SelectMany(inter => inter.GetMethods(bindingFlags)).Where(m => !m.IsSpecialName);
            foreach (var method in methods)
            {
                DefineMethod(typeBuilder, baseType, targetProperty.GetMethod, resourceType, method);
            }

            // Define events
            var eventListener = new Dictionary<EventInfo, MethodBuilder>();
            var events = interfaces.SelectMany(inter => inter.GetEvents(bindingFlags));
            foreach (var eventInfo in events)
            {
                eventListener[eventInfo] = DefineEvent(typeBuilder, baseType, eventInfo);
            }

            // Override Attach
            OverrideAttach(typeBuilder, baseType, targetProperty.GetMethod, resourceType, eventListener);

            // Override Detach
            OverrideDetach(typeBuilder, baseType, targetProperty.GetMethod, resourceType, eventListener);

            return typeBuilder.CreateType();
        }

        /// <summary>
        /// Define the constructor of the proxy
        /// </summary>
        private static void DefineConstructor(TypeBuilder typeBuilder, Type baseType, Type resourceType)
        {
            var constructorArgs = new[] { resourceType, typeof(IResourceTypeController) };
            var baseConstructor = baseType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, constructorArgs, null);

            var constructor = typeBuilder.DefineConstructor(MethodAttributes.Public, baseConstructor.CallingConvention, constructorArgs);

            var generator = constructor.GetILGenerator();
            generator.Emit(OpCodes.Ldarg_0); // Load 'this' onto the stack
            generator.Emit(OpCodes.Ldarg_1); // Load 'target' argument onto the stack
            generator.Emit(OpCodes.Ldarg_2); // Load 'typeController' argument onto the stack
            generator.Emit(OpCodes.Call, baseConstructor); // Call the base constructor with the 'target' and 'typeController' argument
            generator.Emit(OpCodes.Ret); // Return call and end
        }

        /// <summary>
        /// Define a property on the proxy
        /// </summary>
        private static void DefineProperty(TypeBuilder typeBuilder, Type baseType, MethodInfo targetGetter, Type targetType, IGrouping<string, PropertyInfo> propertyGroup)
        {
            var propertyOnTarget = targetType.GetProperty(propertyGroup.Key);
            var propertyBuilder = typeBuilder.DefineProperty(propertyGroup.Key, propertyOnTarget.Attributes, propertyOnTarget.PropertyType, null);

            // Flag if this property references another public resource
            var isResourceReference = IsResourceReference(propertyOnTarget.PropertyType);

            if (propertyOnTarget.CanRead)
            {
                var getterBuilder = typeBuilder.DefineMethod($"get_{propertyGroup.Key}", SpecialNameAttributes, propertyOnTarget.PropertyType, Type.EmptyTypes);
                var generator = getterBuilder.GetILGenerator();
                generator.Emit(OpCodes.Ldarg_0);
                if (isResourceReference)
                    generator.Emit(OpCodes.Dup); // Duplicate 'this' to call convert later
                generator.Emit(OpCodes.Call, targetGetter); // Get the 'Target' object using the property getter
                generator.Emit(OpCodes.Callvirt, propertyOnTarget.GetMethod); // Call 'get' on the target
                if (isResourceReference)
                {
                    // Call convert with the object on the stack to convert it into a proxy
                    var convertMethod = Convert(baseType, propertyOnTarget.PropertyType);
                    generator.Emit(OpCodes.Call, convertMethod);
                }
                generator.Emit(OpCodes.Ret);

                // Link getter to property and interface
                propertyBuilder.SetGetMethod(getterBuilder);
                // Link this getter to all interfaces that define it
                foreach (var property in propertyGroup.Where(p => p.CanRead))
                {
                    typeBuilder.DefineMethodOverride(getterBuilder, property.GetMethod);
                }
            }

            if (propertyOnTarget.CanWrite)
            {
                var setterBuilder = typeBuilder.DefineMethod($"set_{propertyOnTarget.Name}", SpecialNameAttributes, null, new[] { propertyOnTarget.PropertyType });
                var generator = setterBuilder.GetILGenerator();
                generator.Emit(OpCodes.Ldarg_0); // Load 'this onto the stack
                generator.Emit(OpCodes.Call, targetGetter); // Call the 'Target' getter and load Target on stack

                generator.Emit(OpCodes.Ldarg_1); // Load 'value' onto the stack
                if (isResourceReference) // Replace proxy with real resource again
                    ExtractTargetFromStack(generator, propertyOnTarget.PropertyType);

                generator.Emit(OpCodes.Callvirt, propertyOnTarget.SetMethod); // Call 'set' on the target
                generator.Emit(OpCodes.Ret);

                // Link setter to property and interface
                propertyBuilder.SetSetMethod(setterBuilder);
                // Link this setter to all interfaces that can write
                foreach (var property in propertyGroup.Where(p => p.CanWrite))
                {
                    typeBuilder.DefineMethodOverride(setterBuilder, property.SetMethod);
                }
            }
        }

        /// <summary>
        /// Cast the topmost value of the stack to <see cref="IResourceProxy"/> and extract the original resource
        /// </summary>
        private static void ExtractTargetFromStack(ILGenerator generator, Type targetType)
        {
            var interfaceType = typeof(IResourceProxy);
            generator.Emit(OpCodes.Castclass, interfaceType); // Cast value to proxy
            var proxyTarget = interfaceType.GetProperty(nameof(IResourceProxy.Target)).GetMethod;
            generator.Emit(OpCodes.Call, proxyTarget); // Call getter on Proxy.Target to load the real resource on the stack
            generator.Emit(OpCodes.Castclass, targetType); // Cast resource to the property type
        }

        /// <summary>
        /// Define a method on the proxy that forwards the call the target 
        /// </summary>
        private static void DefineMethod(TypeBuilder typeBuilder, Type baseType, MethodInfo targetGetter, Type targetType, MethodInfo method)
        {
            var parameters = method.GetParameters();
            var argumentTypes = parameters.Select(p => p.ParameterType).ToArray();
            var methodOnTarget = targetType.GetMethod(method.Name, argumentTypes);

            var methodAttributes = MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.NewSlot;
            var methodBuilder = typeBuilder.DefineMethod(method.Name, methodAttributes, method.ReturnType, argumentTypes);

            var isResourceReference = IsResourceReference(method.ReturnType);

            // Build the method code
            var generator = methodBuilder.GetILGenerator();
            generator.Emit(OpCodes.Ldarg_0); // Load 'this'
            if (isResourceReference) // Duplicate 'this' on the stack because we need it for the Convert call
                generator.Emit(OpCodes.Dup); 
            generator.Emit(OpCodes.Call, targetGetter); // Load 'Parent' field
            // Load all arguments
            foreach (var parameter in parameters)
            {
                generator.Emit(OpCodes.Ldarg, parameter.Position + 1); // Parameters are zero based, but in IL argument 0 of every instance method is 'this'
                // Extract resource if the parameter is a proxy
                if (IsResourceReference(parameter.ParameterType)) 
                    ExtractTargetFromStack(generator, parameter.ParameterType);
            }
            // Call method on target
            generator.Emit(OpCodes.Callvirt, methodOnTarget);
            if (isResourceReference) // Convert return value to proxy
            {
                var convertMethod = Convert(baseType, method.ReturnType);
                generator.Emit(OpCodes.Call, convertMethod);
            }
            // End invocation
            generator.Emit(OpCodes.Ret);

            // Define overide to the interface
            typeBuilder.DefineMethodOverride(methodBuilder, method);
        }

        /// <summary>
        /// Determine if the return type indicates a resource reference
        /// </summary>
        private static bool IsResourceReference(Type returnType)
        {
            return typeof(IResource).IsAssignableFrom(returnType) | typeof(IEnumerable<IResource>).IsAssignableFrom(returnType);
        }

        /// <summary>
        /// Get the type controller in case another resource was referenced
        /// </summary>
        /// <returns></returns>
        private static MethodInfo Convert(Type baseType, Type propertyType)
        {
            // Target field for property and method forwarding
            Type elementType;
            string methodName;
            if (typeof(IResource).IsAssignableFrom(propertyType))
            {
                elementType = propertyType;
                methodName = nameof(ResourceProxy<PublicResource>.Convert);
            }
            else
            {
                elementType = propertyType.GetGenericArguments()[0];
                methodName = nameof(ResourceProxy<PublicResource>.ConvertMany);
            }
            var methodInfo = baseType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            return methodInfo.MakeGenericMethod(elementType);
        }

        /// <summary>
        /// Define an event and the corresponding handler method. This includes defining the field, the event and the 
        /// add_ and remove_ methods. Finally we also add a method used as a listener for the Target event and raise method
        /// for this event.
        /// This code was inspired by: https://stackoverflow.com/questions/13619527/how-do-i-use-the-eventbuilder-to-create-an-event
        /// </summary>
        private static MethodBuilder DefineEvent(TypeBuilder typeBuilder, Type baseType, EventInfo eventInfo)
        {
            // Step 1: Define the field
            var field = typeBuilder.DefineField(eventInfo.Name, eventInfo.EventHandlerType, FieldAttributes.Private);

            // Step 2: Define the event
            var eventBuilder = typeBuilder.DefineEvent(eventInfo.Name, EventAttributes.None, eventInfo.EventHandlerType);

            // Step 3: Define the add
            var addMethod = typeBuilder.DefineMethod($"add_{eventInfo.Name}", SpecialNameAttributes, typeof(void), new[] { eventInfo.EventHandlerType });
            var generator = addMethod.GetILGenerator();
            // Combine current delegate with the new listener
            generator.Emit(OpCodes.Ldarg_0); // Load 'this' onto the stack
            generator.Emit(OpCodes.Dup); // Duplicate 'this' on the stack as we need it to store the field later
            generator.Emit(OpCodes.Ldfld, field); // Load the event field
            generator.Emit(OpCodes.Ldarg_1); // Load the passed delegate
            // Call combine of field and new value
            var combine = typeof(Delegate).GetMethod(nameof(Delegate.Combine), new[] { typeof(Delegate), typeof(Delegate) });
            generator.Emit(OpCodes.Call, combine);
            generator.Emit(OpCodes.Castclass, eventInfo.EventHandlerType); // Cast return value to our delegate type
            generator.Emit(OpCodes.Stfld, field); // Store updated delegate in the event field
            generator.Emit(OpCodes.Ret); // End method
            eventBuilder.SetAddOnMethod(addMethod);
            typeBuilder.DefineMethodOverride(addMethod, eventInfo.AddMethod);

            // Step 4: Define the remove
            var removeMethod = typeBuilder.DefineMethod($"remove_{eventInfo.Name}", SpecialNameAttributes, typeof(void), new[] { eventInfo.EventHandlerType });
            generator = removeMethod.GetILGenerator();
            // Remove delegate from the invocation list
            generator.Emit(OpCodes.Ldarg_0); // Load 'this' onto the stack
            generator.Emit(OpCodes.Dup); // Duplicate 'this' on the stack as we need it to store the field later
            generator.Emit(OpCodes.Ldfld, field); // Load the event field
            generator.Emit(OpCodes.Ldarg_1); // Load the delegate that shall be removed
            // Call remove of current field and the passed value
            var remove = typeof(Delegate).GetMethod(nameof(Delegate.Remove), new[] { typeof(Delegate), typeof(Delegate) });
            generator.Emit(OpCodes.Call, remove);
            generator.Emit(OpCodes.Castclass, eventInfo.EventHandlerType);
            generator.Emit(OpCodes.Stfld, field); // Store updated delegate in the event field
            generator.Emit(OpCodes.Ret); // End method
            eventBuilder.SetRemoveOnMethod(removeMethod);
            typeBuilder.DefineMethodOverride(removeMethod, eventInfo.RemoveMethod);

            // Step 5: Define caller method to raise the event
            var handlerType = eventInfo.EventHandlerType;
            var argument = handlerType == typeof(EventHandler) ? typeof(EventArgs) : handlerType.GetGenericArguments()[0];
            var methodAttributes = MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.NewSlot;
            var raiseMethod = typeBuilder.DefineMethod($"On{eventInfo.Name}", methodAttributes, typeof(void), new[] { typeof(object), argument });
            generator = raiseMethod.GetILGenerator();
            var returnLabel = generator.DefineLabel(); // Branch label if the field is null
            generator.DeclareLocal(eventInfo.EventHandlerType); // Local to store the handler for null comparison
            generator.Emit(OpCodes.Ldarg_0); // Load 'this' onto the stack
            generator.Emit(OpCodes.Ldfld, field); // Load the event field
            generator.Emit(OpCodes.Stloc_0); // Store field to local
            generator.Emit(OpCodes.Ldloc_0); // Load again from local
            generator.Emit(OpCodes.Brfalse, returnLabel); // Check field for 'null' and jump to the end
            generator.Emit(OpCodes.Ldloc_0); // Restore delegate from local because the null check popped it off
            generator.Emit(OpCodes.Ldarg_0); // Load 'this' to replace sender with proxy
            if (IsResourceReference(argument)) // Convert argument to proxy
            {
                generator.Emit(OpCodes.Dup); // Duplicate this for the Convert method
                generator.Emit(OpCodes.Ldarg_2); // Load 1. argument to convert to proxy
                var convertMethod = Convert(baseType, argument);
                generator.Emit(OpCodes.Call, convertMethod); // Convert argument to proxy
            }
            else
            {
                generator.Emit(OpCodes.Ldarg_2); // Load 1. argument for the EventHandler<T>
            }
            generator.Emit(OpCodes.Callvirt, eventInfo.EventHandlerType.GetMethod("Invoke")); // Call invoke on the delegate
            generator.MarkLabel(returnLabel); // Insert the return marker for the null check
            generator.Emit(OpCodes.Ret); // End method
            eventBuilder.SetRaiseMethod(raiseMethod);

            return raiseMethod; // Return the raise method to register it for the 'Target' event
        }

        /// <summary>
        /// Override <see cref="ResourceProxy{T}.Attach"/> and register event handlers
        /// </summary>
        private static void OverrideAttach(TypeBuilder typeBuilder, Type baseType, MethodInfo targetGetter, Type targetType, Dictionary<EventInfo, MethodBuilder> eventHandlers)
        {
            var methodAttributes = MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.HideBySig;
            var methodBuilder = typeBuilder.DefineMethod(nameof(IResourceProxy.Attach), methodAttributes, typeof(void), Type.EmptyTypes);

            var generator = methodBuilder.GetILGenerator();
            generator.Emit(OpCodes.Ldarg_0); // Load 'this'
            var baseAttach = baseType.GetMethod(nameof(IResourceProxy.Attach));
            generator.Emit(OpCodes.Call, baseAttach); // Non-virtual call on base.Attach()
            // Link event handler for each event on the target object
            foreach (var eventHandler in eventHandlers)
            {
                generator.Emit(OpCodes.Ldarg_0); // Load 'this'
                generator.Emit(OpCodes.Call, targetGetter); // Load 'Parent' field

                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldftn, eventHandler.Value); // Load pointer to 'On{Event}'

                var eventInfo = eventHandler.Key;
                var delegateConstructor = eventInfo.EventHandlerType.GetConstructor(new[] { typeof(object), typeof(IntPtr) });
                generator.Emit(OpCodes.Newobj, delegateConstructor); // Build delegate from this and method pointer in previous step

                var eventOnTarget = targetType.GetEvent(eventInfo.Name);
                generator.Emit(OpCodes.Callvirt, eventOnTarget.AddMethod); // Register the new delegate on the Parent.{Event}
            }

            generator.Emit(OpCodes.Ret); // Finish method
        }

        /// <summary>
        /// Override <see cref="ResourceProxy{T}.Detach"/> and unregister event handlers
        /// </summary>
        private static void OverrideDetach(TypeBuilder typeBuilder, Type baseType, MethodInfo targetGetter, Type targetType, Dictionary<EventInfo, MethodBuilder> eventHandlers)
        {
            var methodAttributes = MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.HideBySig;
            var methodBuilder = typeBuilder.DefineMethod(nameof(IResourceProxy.Detach), methodAttributes, typeof(void), Type.EmptyTypes);

            var generator = methodBuilder.GetILGenerator();
            // Unregister event handler for each event on the target object
            foreach (var eventHandler in eventHandlers)
            {
                generator.Emit(OpCodes.Ldarg_0); // Load 'this'
                generator.Emit(OpCodes.Call, targetGetter); // Load 'Target' field

                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldftn, eventHandler.Value); // Load pointer to 'On{Event}'

                var eventInfo = eventHandler.Key;
                var delegateConstructor = eventInfo.EventHandlerType.GetConstructor(new[] { typeof(object), typeof(IntPtr) });
                generator.Emit(OpCodes.Newobj, delegateConstructor); // Build delegate from this and method pointer in previous step

                var eventOnTarget = targetType.GetEvent(eventInfo.Name);
                generator.Emit(OpCodes.Callvirt, eventOnTarget.RemoveMethod); // Unregister proxy from Parent.{Event}
            }

            // Call base.Detach()
            generator.Emit(OpCodes.Ldarg_0); // Load 'this'
            var baseAttach = baseType.GetMethod(nameof(IResourceProxy.Detach));
            generator.Emit(OpCodes.Call, baseAttach); // Non-virtual call on base.Attach()

            generator.Emit(OpCodes.Ret); // Finish method
        }
    }
}