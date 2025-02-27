// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace System
{
    [ClassInterface(ClassInterfaceType.None)]
    [ComVisible(true)]
    public abstract partial class Delegate : ICloneable, ISerializable
    {
        // _target is the object we will invoke on
        internal object? _target; // Initialized by VM as needed; null if static delegate

        // MethodBase, either cached after first request or assigned from a DynamicMethod
        // For open delegates to collectible types, this may be a LoaderAllocator object
        internal object? _methodBase; // Initialized by VM as needed

        // _methodPtr is a pointer to the method we will invoke
        // It could be a small thunk if this is a static or UM call
        internal IntPtr _methodPtr;

        // In the case of a static method passed to a delegate, this field stores
        // whatever _methodPtr would have stored: and _methodPtr points to a
        // small thunk which removes the "this" pointer before going on
        // to _methodPtrAux.
        internal IntPtr _methodPtrAux;

        // This constructor is called from the class generated by the
        //  compiler generated code
        protected Delegate(object target, string method)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            if (method == null)
                throw new ArgumentNullException(nameof(method));

            // This API existed in v1/v1.1 and only expected to create closed
            // instance delegates. Constrain the call to BindToMethodName to
            // such and don't allow relaxed signature matching (which could make
            // the choice of target method ambiguous) for backwards
            // compatibility. The name matching was case sensitive and we
            // preserve that as well.
            if (!BindToMethodName(target, (RuntimeType)target.GetType(), method,
                                  DelegateBindingFlags.InstanceMethodOnly |
                                  DelegateBindingFlags.ClosedDelegateOnly))
                throw new ArgumentException(SR.Arg_DlgtTargMeth);
        }

        // This constructor is called from a class to generate a
        // delegate based upon a static method name and the Type object
        // for the class defining the method.
        protected Delegate(Type target, string method)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            if (target.ContainsGenericParameters)
                throw new ArgumentException(SR.Arg_UnboundGenParam, nameof(target));

            if (method == null)
                throw new ArgumentNullException(nameof(method));

            if (!(target is RuntimeType rtTarget))
                throw new ArgumentException(SR.Argument_MustBeRuntimeType, nameof(target));

            // This API existed in v1/v1.1 and only expected to create open
            // static delegates. Constrain the call to BindToMethodName to such
            // and don't allow relaxed signature matching (which could make the
            // choice of target method ambiguous) for backwards compatibility.
            // The name matching was case insensitive (no idea why this is
            // different from the constructor above) and we preserve that as
            // well.
            BindToMethodName(null, rtTarget, method,
                             DelegateBindingFlags.StaticMethodOnly |
                             DelegateBindingFlags.OpenDelegateOnly |
                             DelegateBindingFlags.CaselessMatching);
        }

        protected virtual object? DynamicInvokeImpl(object?[]? args)
        {
            RuntimeMethodHandleInternal method = new RuntimeMethodHandleInternal(GetInvokeMethod());
            RuntimeMethodInfo invoke = (RuntimeMethodInfo)RuntimeType.GetMethodBase((RuntimeType)this.GetType(), method)!;

            return invoke.Invoke(this, BindingFlags.Default, null, args, null);
        }


        public override bool Equals(object? obj)
        {
            if (obj == null || !InternalEqualTypes(this, obj))
                return false;

            Delegate d = (Delegate)obj;

            // do an optimistic check first. This is hopefully cheap enough to be worth
            if (_target == d._target && _methodPtr == d._methodPtr && _methodPtrAux == d._methodPtrAux)
                return true;

            // even though the fields were not all equals the delegates may still match
            // When target carries the delegate itself the 2 targets (delegates) may be different instances
            // but the delegates are logically the same
            // It may also happen that the method pointer was not jitted when creating one delegate and jitted in the other
            // if that's the case the delegates may still be equals but we need to make a more complicated check

            if (_methodPtrAux == IntPtr.Zero)
            {
                if (d._methodPtrAux != IntPtr.Zero)
                    return false; // different delegate kind
                // they are both closed over the first arg
                if (_target != d._target)
                    return false;
                // fall through method handle check
            }
            else
            {
                if (d._methodPtrAux == IntPtr.Zero)
                    return false; // different delegate kind

                // Ignore the target as it will be the delegate instance, though it may be a different one
                /*
                if (_methodPtr != d._methodPtr)
                    return false;
                    */

                if (_methodPtrAux == d._methodPtrAux)
                    return true;
                // fall through method handle check
            }

            // method ptrs don't match, go down long path
            //
            if (_methodBase == null || d._methodBase == null || !(_methodBase is MethodInfo) || !(d._methodBase is MethodInfo))
                return Delegate.InternalEqualMethodHandles(this, d);
            else
                return _methodBase.Equals(d._methodBase);
        }

        public override int GetHashCode()
        {
            //
            // this is not right in the face of a method being jitted in one delegate and not in another
            // in that case the delegate is the same and Equals will return true but GetHashCode returns a
            // different hashcode which is not true.
            /*
            if (_methodPtrAux == IntPtr.Zero)
                return unchecked((int)((long)this._methodPtr));
            else
                return unchecked((int)((long)this._methodPtrAux));
            */
            if (_methodPtrAux == IntPtr.Zero)
                return ( _target != null ? RuntimeHelpers.GetHashCode(_target) * 33 : 0) + GetType().GetHashCode();
            else
                return GetType().GetHashCode();
        }

        protected virtual MethodInfo GetMethodImpl()
        {
            if ((_methodBase == null) || !(_methodBase is MethodInfo))
            {
                IRuntimeMethodInfo method = FindMethodHandle();
                RuntimeType? declaringType = RuntimeMethodHandle.GetDeclaringType(method);
                // need a proper declaring type instance method on a generic type
                if (RuntimeTypeHandle.IsGenericTypeDefinition(declaringType) || RuntimeTypeHandle.HasInstantiation(declaringType))
                {
                    bool isStatic = (RuntimeMethodHandle.GetAttributes(method) & MethodAttributes.Static) != (MethodAttributes)0;
                    if (!isStatic)
                    {
                        if (_methodPtrAux == IntPtr.Zero)
                        {
                            // The target may be of a derived type that doesn't have visibility onto the
                            // target method. We don't want to call RuntimeType.GetMethodBase below with that
                            // or reflection can end up generating a MethodInfo where the ReflectedType cannot
                            // see the MethodInfo itself and that breaks an important invariant. But the
                            // target type could include important generic type information we need in order
                            // to work out what the exact instantiation of the method's declaring type is. So
                            // we'll walk up the inheritance chain (which will yield exactly instantiated
                            // types at each step) until we find the declaring type. Since the declaring type
                            // we get from the method is probably shared and those in the hierarchy we're
                            // walking won't be we compare using the generic type definition forms instead.
                            Type? currentType = _target!.GetType();
                            Type targetType = declaringType.GetGenericTypeDefinition();
                            while (currentType != null)
                            {
                                if (currentType.IsGenericType &&
                                    currentType.GetGenericTypeDefinition() == targetType)
                                {
                                    declaringType = currentType as RuntimeType;
                                    break;
                                }
                                currentType = currentType.BaseType;
                            }

                            // RCWs don't need to be "strongly-typed" in which case we don't find a base type
                            // that matches the declaring type of the method. This is fine because interop needs
                            // to work with exact methods anyway so declaringType is never shared at this point.
                            Debug.Assert(currentType != null || _target.GetType().IsCOMObject, "The class hierarchy should declare the method");
                        }
                        else
                        {
                            // it's an open one, need to fetch the first arg of the instantiation
                            MethodInfo invoke = this.GetType().GetMethod("Invoke")!;
                            declaringType = (RuntimeType)invoke.GetParameters()[0].ParameterType;
                        }
                    }
                }
                _methodBase = (MethodInfo)RuntimeType.GetMethodBase(declaringType, method)!;
            }
            return (MethodInfo)_methodBase;
        }

        public object? Target => GetTarget();

        // V1 API.
        public static Delegate? CreateDelegate(Type type, object target, string method, bool ignoreCase, bool throwOnBindFailure)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            if (method == null)
                throw new ArgumentNullException(nameof(method));

            if (!(type is RuntimeType rtType))
                throw new ArgumentException(SR.Argument_MustBeRuntimeType, nameof(type));
            if (!rtType.IsDelegate())
                throw new ArgumentException(SR.Arg_MustBeDelegate, nameof(type));

            Delegate d = InternalAlloc(rtType);
            // This API existed in v1/v1.1 and only expected to create closed
            // instance delegates. Constrain the call to BindToMethodName to such
            // and don't allow relaxed signature matching (which could make the
            // choice of target method ambiguous) for backwards compatibility.
            // We never generate a closed over null delegate and this is
            // actually enforced via the check on target above, but we pass
            // NeverCloseOverNull anyway just for clarity.
            if (!d.BindToMethodName(target, (RuntimeType)target.GetType(), method,
                                    DelegateBindingFlags.InstanceMethodOnly |
                                    DelegateBindingFlags.ClosedDelegateOnly |
                                    DelegateBindingFlags.NeverCloseOverNull |
                                    (ignoreCase ? DelegateBindingFlags.CaselessMatching : 0)))
            {
                if (throwOnBindFailure)
                    throw new ArgumentException(SR.Arg_DlgtTargMeth);

                return null;
            }

            return d;
        }

        // V1 API.
        public static Delegate? CreateDelegate(Type type, Type target, string method, bool ignoreCase, bool throwOnBindFailure)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            if (target.ContainsGenericParameters)
                throw new ArgumentException(SR.Arg_UnboundGenParam, nameof(target));
            if (method == null)
                throw new ArgumentNullException(nameof(method));

            if (!(type is RuntimeType rtType))
                throw new ArgumentException(SR.Argument_MustBeRuntimeType, nameof(type));
            if (!(target is RuntimeType rtTarget))
                throw new ArgumentException(SR.Argument_MustBeRuntimeType, nameof(target));

            if (!rtType.IsDelegate())
                throw new ArgumentException(SR.Arg_MustBeDelegate, nameof(type));

            Delegate d = InternalAlloc(rtType);
            // This API existed in v1/v1.1 and only expected to create open
            // static delegates. Constrain the call to BindToMethodName to such
            // and don't allow relaxed signature matching (which could make the
            // choice of target method ambiguous) for backwards compatibility.
            if (!d.BindToMethodName(null, rtTarget, method,
                                    DelegateBindingFlags.StaticMethodOnly |
                                    DelegateBindingFlags.OpenDelegateOnly |
                                    (ignoreCase ? DelegateBindingFlags.CaselessMatching : 0)))
            {
                if (throwOnBindFailure)
                    throw new ArgumentException(SR.Arg_DlgtTargMeth);

                return null;
            }

            return d;
        }

        // V1 API.
        public static Delegate? CreateDelegate(Type type, MethodInfo method, bool throwOnBindFailure)
        {
            // Validate the parameters.
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            if (method == null)
                throw new ArgumentNullException(nameof(method));

            if (!(type is RuntimeType rtType))
                throw new ArgumentException(SR.Argument_MustBeRuntimeType, nameof(type));

            if (!(method is RuntimeMethodInfo rmi))
                throw new ArgumentException(SR.Argument_MustBeRuntimeMethodInfo, nameof(method));

            if (!rtType.IsDelegate())
                throw new ArgumentException(SR.Arg_MustBeDelegate, nameof(type));

            // This API existed in v1/v1.1 and only expected to create closed
            // instance delegates. Constrain the call to BindToMethodInfo to
            // open delegates only for backwards compatibility. But we'll allow
            // relaxed signature checking and open static delegates because
            // there's no ambiguity there (the caller would have to explicitly
            // pass us a static method or a method with a non-exact signature
            // and the only change in behavior from v1.1 there is that we won't
            // fail the call).
            Delegate? d = CreateDelegateInternal(
                rtType,
                rmi,
                null,
                DelegateBindingFlags.OpenDelegateOnly | DelegateBindingFlags.RelaxedSignature);

            if (d == null && throwOnBindFailure)
                throw new ArgumentException(SR.Arg_DlgtTargMeth);

            return d;
        }

        // V2 API.
        public static Delegate? CreateDelegate(Type type, object? firstArgument, MethodInfo method, bool throwOnBindFailure)
        {
            // Validate the parameters.
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            if (method == null)
                throw new ArgumentNullException(nameof(method));

            if (!(type is RuntimeType rtType))
                throw new ArgumentException(SR.Argument_MustBeRuntimeType, nameof(type));

            if (!(method is RuntimeMethodInfo rmi))
                throw new ArgumentException(SR.Argument_MustBeRuntimeMethodInfo, nameof(method));

            if (!rtType.IsDelegate())
                throw new ArgumentException(SR.Arg_MustBeDelegate, nameof(type));

            // This API is new in Whidbey and allows the full range of delegate
            // flexability (open or closed delegates binding to static or
            // instance methods with relaxed signature checking. The delegate
            // can also be closed over null. There's no ambiguity with all these
            // options since the caller is providing us a specific MethodInfo.
            Delegate? d = CreateDelegateInternal(
                rtType,
                rmi,
                firstArgument,
                DelegateBindingFlags.RelaxedSignature);

            if (d == null && throwOnBindFailure)
                throw new ArgumentException(SR.Arg_DlgtTargMeth);

            return d;
        }

        //
        // internal implementation details (FCALLS and utilities)
        //

        // V2 internal API.
        internal static Delegate CreateDelegateNoSecurityCheck(Type type, object? target, RuntimeMethodHandle method)
        {
            // Validate the parameters.
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            if (method.IsNullHandle())
                throw new ArgumentNullException(nameof(method));

            if (!(type is RuntimeType rtType))
                throw new ArgumentException(SR.Argument_MustBeRuntimeType, nameof(type));

            if (!rtType.IsDelegate())
                throw new ArgumentException(SR.Arg_MustBeDelegate, nameof(type));

            // Initialize the method...
            Delegate d = InternalAlloc(rtType);
            // This is a new internal API added in Whidbey. Currently it's only
            // used by the dynamic method code to generate a wrapper delegate.
            // Allow flexible binding options since the target method is
            // unambiguously provided to us.

            if (!d.BindToMethodInfo(target,
                                    method.GetMethodInfo(),
                                    RuntimeMethodHandle.GetDeclaringType(method.GetMethodInfo()),
                                    DelegateBindingFlags.RelaxedSignature | DelegateBindingFlags.SkipSecurityChecks))
                throw new ArgumentException(SR.Arg_DlgtTargMeth);
            return d;
        }

        internal static Delegate? CreateDelegateInternal(RuntimeType rtType, RuntimeMethodInfo rtMethod, object? firstArgument, DelegateBindingFlags flags)
        {
            Delegate d = InternalAlloc(rtType);

            if (d.BindToMethodInfo(firstArgument, rtMethod, rtMethod.GetDeclaringTypeInternal(), flags))
                return d;
            else
                return null;
        }

        //
        // internal implementation details (FCALLS and utilities)
        //

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private extern bool BindToMethodName(object? target, RuntimeType methodType, string method, DelegateBindingFlags flags);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private extern bool BindToMethodInfo(object? target, IRuntimeMethodInfo method, RuntimeType methodType, DelegateBindingFlags flags);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static extern MulticastDelegate InternalAlloc(RuntimeType type);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal static extern MulticastDelegate InternalAllocLike(Delegate d);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal static extern bool InternalEqualTypes(object a, object b);

        // Used by the ctor. Do not call directly.
        // The name of this function will appear in managed stacktraces as delegate constructor.
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private extern void DelegateConstruct(object target, IntPtr slot);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal extern IntPtr GetMulticastInvoke();

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal extern IntPtr GetInvokeMethod();

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal extern IRuntimeMethodInfo FindMethodHandle();

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal static extern bool InternalEqualMethodHandles(Delegate left, Delegate right);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal extern IntPtr AdjustTarget(object target, IntPtr methodPtr);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal extern IntPtr GetCallStub(IntPtr methodPtr);

        internal virtual object? GetTarget()
        {
            return (_methodPtrAux == IntPtr.Zero) ? _target : null;
        }

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal static extern bool CompareUnmanagedFunctionPtrs(Delegate d1, Delegate d2);
    }

    // These flags effect the way BindToMethodInfo and BindToMethodName are allowed to bind a delegate to a target method. Their
    // values must be kept in sync with the definition in vm\comdelegate.h.
    internal enum DelegateBindingFlags
    {
        StaticMethodOnly = 0x00000001, // Can only bind to static target methods
        InstanceMethodOnly = 0x00000002, // Can only bind to instance (including virtual) methods
        OpenDelegateOnly = 0x00000004, // Only allow the creation of delegates open over the 1st argument
        ClosedDelegateOnly = 0x00000008, // Only allow the creation of delegates closed over the 1st argument
        NeverCloseOverNull = 0x00000010, // A null target will never been considered as a possible null 1st argument
        CaselessMatching = 0x00000020, // Use case insensitive lookup for methods matched by name
        SkipSecurityChecks = 0x00000040, // Skip security checks (visibility, link demand etc.)
        RelaxedSignature = 0x00000080, // Allow relaxed signature matching (co/contra variance)
    }
}
