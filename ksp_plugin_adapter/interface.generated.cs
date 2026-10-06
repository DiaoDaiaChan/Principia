using System;
using System.Linq;
using System.Runtime.InteropServices;
namespace principia {
namespace ksp_plugin_adapter {
	internal enum CoordinateSystem : byte
	{
		CARTESIAN_TNB = 1,
		SPHERICAL_TNB,
		SPHERICAL_NBT,
		SPHERICAL_BTN
	}
	internal partial class NavigationFrameParameters : IReferenceFrameParameters
	{
		internal class Marshaler : MonoMarshaler
		{
			internal struct Representation
			{
				public int extension;

				public int centre_index;

				public int primary_index;

				public int secondary_index;
			}

			private static readonly Marshaler instance_ = new Marshaler();

			public static Representation ManagedToNative(NavigationFrameParameters value)
			{
				return new Representation
				{
					extension = value.extension,
					centre_index = value.centre_index,
					primary_index = value.primary_index,
					secondary_index = value.secondary_index
				};
			}

			public static NavigationFrameParameters NativeToManaged(Representation representation)
			{
				return new NavigationFrameParameters
				{
					extension = representation.extension,
					centre_index = representation.centre_index,
					primary_index = representation.primary_index,
					secondary_index = representation.secondary_index
				};
			}

			public static ICustomMarshaler GetInstance(string s)
			{
				return instance_;
			}

			public override void CleanUpNativeDataImplementation(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				Marshal.FreeHGlobal(native_data);
			}

			public override IntPtr MarshalManagedToNativeImplementation(object managed_object)
			{
				if (!(managed_object is NavigationFrameParameters value))
				{
					throw new NotSupportedException();
				}
				Representation structure = ManagedToNative(value);
				IntPtr intPtr = Marshal.AllocHGlobal(Marshal.SizeOf(structure));
				Marshal.StructureToPtr(structure, intPtr, fDeleteOld: false);
				return intPtr;
			}

			public override object MarshalNativeToManaged(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				return NativeToManaged(representation);
			}
		}

		public int extension;

		public int centre_index;

		private int primary_index;

		private int secondary_index;

	}
	public struct SphericalCoordinates
	{
		public double radius;

		public double latitude_in_degrees;

		public double longitude_in_degrees;
	}
	public partial struct XYZ
	{
		public double x;

		public double y;

		public double z;

	}
	internal struct Intensity
	{
		private byte coordinate_system_;

		public XYZ xyz;

		public SphericalCoordinates spherical_coordinates;

		public CoordinateSystem coordinate_system
		{
			get
			{
				return (CoordinateSystem)coordinate_system_;
			}
			set
			{
				coordinate_system_ = (byte)value;
			}
		}
	}
	internal struct AdaptiveStepParameters
	{
		public long integrator_kind;

		public long max_steps;

		public double length_integration_tolerance;

		public double speed_integration_tolerance;
	}
	internal class BodyGeopotentialElement
	{
		internal class Marshaler : MonoMarshaler
		{
			internal struct Representation
			{
				public IntPtr degree;

				public IntPtr order;

				public IntPtr cos;

				public IntPtr j;

				public IntPtr sin;
			}

			private static readonly Marshaler instance_ = new Marshaler();

			public static Representation ManagedToNative(BodyGeopotentialElement value)
			{
				return new Representation
				{
					degree = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.degree),
					order = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.order),
					cos = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.cos),
					j = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.j),
					sin = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.sin)
				};
			}

			public static BodyGeopotentialElement NativeToManaged(Representation representation)
			{
				BodyGeopotentialElement bodyGeopotentialElement = new BodyGeopotentialElement();
				bodyGeopotentialElement.degree = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.degree) as string;
				bodyGeopotentialElement.order = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.order) as string;
				bodyGeopotentialElement.cos = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.cos) as string;
				bodyGeopotentialElement.j = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.j) as string;
				bodyGeopotentialElement.sin = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.sin) as string;
				return bodyGeopotentialElement;
			}

			public static ICustomMarshaler GetInstance(string s)
			{
				return instance_;
			}

			public override void CleanUpNativeDataImplementation(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.degree);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.order);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.cos);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.j);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.sin);
				Marshal.FreeHGlobal(native_data);
			}

			public override IntPtr MarshalManagedToNativeImplementation(object managed_object)
			{
				if (!(managed_object is BodyGeopotentialElement value))
				{
					throw new NotSupportedException();
				}
				Representation structure = ManagedToNative(value);
				IntPtr intPtr = Marshal.AllocHGlobal(Marshal.SizeOf(structure));
				Marshal.StructureToPtr(structure, intPtr, fDeleteOld: false);
				return intPtr;
			}

			public override object MarshalNativeToManaged(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				return NativeToManaged(representation);
			}
		}

		public string degree;

		public string order;

		public string cos;

		public string j;

		public string sin;
	}
	internal class BodyParameters
	{
		internal class Marshaler : MonoMarshaler
		{
			internal struct Representation
			{
				public IntPtr name;

				public IntPtr gravitational_parameter;

				public IntPtr reference_instant;

				public IntPtr min_radius;

				public IntPtr mean_radius;

				public IntPtr max_radius;

				public IntPtr atmosphere_depth;

				public byte has_ocean;

				public IntPtr axis_right_ascension;

				public IntPtr axis_declination;

				public IntPtr reference_angle;

				public IntPtr angular_frequency;

				public IntPtr reference_radius;

				public IntPtr j2;

				public IntPtr geopotential;
			}

			private static readonly Marshaler instance_ = new Marshaler();

			public static Representation ManagedToNative(BodyParameters value)
			{
				return new Representation
				{
					name = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.name),
					gravitational_parameter = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.gravitational_parameter),
					reference_instant = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.reference_instant),
					min_radius = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.min_radius),
					mean_radius = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.mean_radius),
					max_radius = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.max_radius),
					atmosphere_depth = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.atmosphere_depth),
					has_ocean = (value.has_ocean ? ((byte)1) : ((byte)0)),
					axis_right_ascension = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.axis_right_ascension),
					axis_declination = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.axis_declination),
					reference_angle = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.reference_angle),
					angular_frequency = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.angular_frequency),
					reference_radius = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.reference_radius),
					j2 = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.j2),
					geopotential = RepeatedMarshaler<BodyGeopotentialElement, BodyGeopotentialElement.Marshaler>.GetInstance(null).MarshalManagedToNative(value.geopotential)
				};
			}

			public static BodyParameters NativeToManaged(Representation representation)
			{
				BodyParameters bodyParameters = new BodyParameters();
				bodyParameters.name = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.name) as string;
				bodyParameters.gravitational_parameter = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.gravitational_parameter) as string;
				bodyParameters.reference_instant = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.reference_instant) as string;
				bodyParameters.min_radius = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.min_radius) as string;
				bodyParameters.mean_radius = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.mean_radius) as string;
				bodyParameters.max_radius = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.max_radius) as string;
				bodyParameters.atmosphere_depth = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.atmosphere_depth) as string;
				bodyParameters.has_ocean = representation.has_ocean != 0;
				bodyParameters.axis_right_ascension = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.axis_right_ascension) as string;
				bodyParameters.axis_declination = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.axis_declination) as string;
				bodyParameters.reference_angle = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.reference_angle) as string;
				bodyParameters.angular_frequency = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.angular_frequency) as string;
				bodyParameters.reference_radius = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.reference_radius) as string;
				bodyParameters.j2 = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.j2) as string;
				bodyParameters.geopotential = RepeatedMarshaler<BodyGeopotentialElement, BodyGeopotentialElement.Marshaler>.GetInstance(null).MarshalNativeToManaged(representation.geopotential) as BodyGeopotentialElement[];
				return bodyParameters;
			}

			public static ICustomMarshaler GetInstance(string s)
			{
				return instance_;
			}

			public override void CleanUpNativeDataImplementation(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.name);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.gravitational_parameter);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.reference_instant);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.min_radius);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.mean_radius);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.max_radius);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.atmosphere_depth);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.axis_right_ascension);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.axis_declination);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.reference_angle);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.angular_frequency);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.reference_radius);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.j2);
				RepeatedMarshaler<BodyGeopotentialElement, BodyGeopotentialElement.Marshaler>.GetInstance(null).CleanUpNativeData(representation.geopotential);
				Marshal.FreeHGlobal(native_data);
			}

			public override IntPtr MarshalManagedToNativeImplementation(object managed_object)
			{
				if (!(managed_object is BodyParameters value))
				{
					throw new NotSupportedException();
				}
				Representation structure = ManagedToNative(value);
				IntPtr intPtr = Marshal.AllocHGlobal(Marshal.SizeOf(structure));
				Marshal.StructureToPtr(structure, intPtr, fDeleteOld: false);
				return intPtr;
			}

			public override object MarshalNativeToManaged(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				return NativeToManaged(representation);
			}
		}

		public string name;

		public string gravitational_parameter;

		public string reference_instant;

		public string min_radius;

		public string mean_radius;

		public string max_radius;

		public string atmosphere_depth;

		public bool has_ocean;

		public string axis_right_ascension;

		public string axis_declination;

		public string reference_angle;

		public string angular_frequency;

		public string reference_radius;

		public string j2;

		public BodyGeopotentialElement[] geopotential;
	}
	internal class Burn
	{
		internal class Marshaler : MonoMarshaler
		{
			internal struct Representation
			{
				public double thrust_in_kilonewtons;

				public double specific_impulse_in_seconds_g0;

				public NavigationFrameParameters.Marshaler.Representation frame;

				public double initial_time;

				public Intensity intensity;

				public byte is_inertially_fixed;
			}

			private static readonly Marshaler instance_ = new Marshaler();

			public static Representation ManagedToNative(Burn value)
			{
				return new Representation
				{
					thrust_in_kilonewtons = value.thrust_in_kilonewtons,
					specific_impulse_in_seconds_g0 = value.specific_impulse_in_seconds_g0,
					frame = NavigationFrameParameters.Marshaler.ManagedToNative(value.frame),
					initial_time = value.initial_time,
					intensity = value.intensity,
					is_inertially_fixed = (value.is_inertially_fixed ? ((byte)1) : ((byte)0))
				};
			}

			public static Burn NativeToManaged(Representation representation)
			{
				return new Burn
				{
					thrust_in_kilonewtons = representation.thrust_in_kilonewtons,
					specific_impulse_in_seconds_g0 = representation.specific_impulse_in_seconds_g0,
					frame = NavigationFrameParameters.Marshaler.NativeToManaged(representation.frame),
					initial_time = representation.initial_time,
					intensity = representation.intensity,
					is_inertially_fixed = (representation.is_inertially_fixed != 0)
				};
			}

			public static ICustomMarshaler GetInstance(string s)
			{
				return instance_;
			}

			public override void CleanUpNativeDataImplementation(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				Marshal.FreeHGlobal(native_data);
			}

			public override IntPtr MarshalManagedToNativeImplementation(object managed_object)
			{
				if (!(managed_object is Burn value))
				{
					throw new NotSupportedException();
				}
				Representation structure = ManagedToNative(value);
				IntPtr intPtr = Marshal.AllocHGlobal(Marshal.SizeOf(structure));
				Marshal.StructureToPtr(structure, intPtr, fDeleteOld: false);
				return intPtr;
			}

			public override object MarshalNativeToManaged(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				return NativeToManaged(representation);
			}
		}

		public double thrust_in_kilonewtons;

		public double specific_impulse_in_seconds_g0;

		public NavigationFrameParameters frame;

		public double initial_time;

		public Intensity intensity;

		public bool is_inertially_fixed;
	}
	internal class ConfigurationAccuracyParameters
	{
		internal class Marshaler : MonoMarshaler
		{
			internal struct Representation
			{
				public IntPtr fitting_tolerance;

				public IntPtr geopotential_tolerance;
			}

			private static readonly Marshaler instance_ = new Marshaler();

			public static Representation ManagedToNative(ConfigurationAccuracyParameters value)
			{
				return new Representation
				{
					fitting_tolerance = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.fitting_tolerance),
					geopotential_tolerance = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.geopotential_tolerance)
				};
			}

			public static ConfigurationAccuracyParameters NativeToManaged(Representation representation)
			{
				ConfigurationAccuracyParameters configurationAccuracyParameters = new ConfigurationAccuracyParameters();
				configurationAccuracyParameters.fitting_tolerance = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.fitting_tolerance) as string;
				configurationAccuracyParameters.geopotential_tolerance = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.geopotential_tolerance) as string;
				return configurationAccuracyParameters;
			}

			public static ICustomMarshaler GetInstance(string s)
			{
				return instance_;
			}

			public override void CleanUpNativeDataImplementation(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.fitting_tolerance);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.geopotential_tolerance);
				Marshal.FreeHGlobal(native_data);
			}

			public override IntPtr MarshalManagedToNativeImplementation(object managed_object)
			{
				if (!(managed_object is ConfigurationAccuracyParameters value))
				{
					throw new NotSupportedException();
				}
				Representation structure = ManagedToNative(value);
				IntPtr intPtr = Marshal.AllocHGlobal(Marshal.SizeOf(structure));
				Marshal.StructureToPtr(structure, intPtr, fDeleteOld: false);
				return intPtr;
			}

			public override object MarshalNativeToManaged(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				return NativeToManaged(representation);
			}
		}

		public string fitting_tolerance;

		public string geopotential_tolerance;
	}
	internal class ConfigurationDownsamplingParameters
	{
		internal class Marshaler : MonoMarshaler
		{
			internal struct Representation
			{
				public IntPtr tolerance;
			}

			private static readonly Marshaler instance_ = new Marshaler();

			public static Representation ManagedToNative(ConfigurationDownsamplingParameters value)
			{
				return new Representation
				{
					tolerance = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.tolerance)
				};
			}

			public static ConfigurationDownsamplingParameters NativeToManaged(Representation representation)
			{
				ConfigurationDownsamplingParameters configurationDownsamplingParameters = new ConfigurationDownsamplingParameters();
				configurationDownsamplingParameters.tolerance = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.tolerance) as string;
				return configurationDownsamplingParameters;
			}

			public static ICustomMarshaler GetInstance(string s)
			{
				return instance_;
			}

			public override void CleanUpNativeDataImplementation(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.tolerance);
				Marshal.FreeHGlobal(native_data);
			}

			public override IntPtr MarshalManagedToNativeImplementation(object managed_object)
			{
				if (!(managed_object is ConfigurationDownsamplingParameters value))
				{
					throw new NotSupportedException();
				}
				Representation structure = ManagedToNative(value);
				IntPtr intPtr = Marshal.AllocHGlobal(Marshal.SizeOf(structure));
				Marshal.StructureToPtr(structure, intPtr, fDeleteOld: false);
				return intPtr;
			}

			public override object MarshalNativeToManaged(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				return NativeToManaged(representation);
			}
		}

		public string tolerance;
	}
	internal class ConfigurationFixedStepParameters
	{
		internal class Marshaler : MonoMarshaler
		{
			internal struct Representation
			{
				public IntPtr fixed_step_size_integrator;

				public IntPtr integration_step_size;
			}

			private static readonly Marshaler instance_ = new Marshaler();

			public static Representation ManagedToNative(ConfigurationFixedStepParameters value)
			{
				return new Representation
				{
					fixed_step_size_integrator = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.fixed_step_size_integrator),
					integration_step_size = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.integration_step_size)
				};
			}

			public static ConfigurationFixedStepParameters NativeToManaged(Representation representation)
			{
				ConfigurationFixedStepParameters configurationFixedStepParameters = new ConfigurationFixedStepParameters();
				configurationFixedStepParameters.fixed_step_size_integrator = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.fixed_step_size_integrator) as string;
				configurationFixedStepParameters.integration_step_size = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.integration_step_size) as string;
				return configurationFixedStepParameters;
			}

			public static ICustomMarshaler GetInstance(string s)
			{
				return instance_;
			}

			public override void CleanUpNativeDataImplementation(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.fixed_step_size_integrator);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.integration_step_size);
				Marshal.FreeHGlobal(native_data);
			}

			public override IntPtr MarshalManagedToNativeImplementation(object managed_object)
			{
				if (!(managed_object is ConfigurationFixedStepParameters value))
				{
					throw new NotSupportedException();
				}
				Representation structure = ManagedToNative(value);
				IntPtr intPtr = Marshal.AllocHGlobal(Marshal.SizeOf(structure));
				Marshal.StructureToPtr(structure, intPtr, fDeleteOld: false);
				return intPtr;
			}

			public override object MarshalNativeToManaged(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				return NativeToManaged(representation);
			}
		}

		public string fixed_step_size_integrator;

		public string integration_step_size;
	}
	internal class ConfigurationAdaptiveStepParameters
	{
		internal class Marshaler : MonoMarshaler
		{
			internal struct Representation
			{
				public IntPtr adaptive_step_size_integrator;

				public IntPtr length_integration_tolerance;

				public IntPtr speed_integration_tolerance;
			}

			private static readonly Marshaler instance_ = new Marshaler();

			public static Representation ManagedToNative(ConfigurationAdaptiveStepParameters value)
			{
				return new Representation
				{
					adaptive_step_size_integrator = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.adaptive_step_size_integrator),
					length_integration_tolerance = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.length_integration_tolerance),
					speed_integration_tolerance = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.speed_integration_tolerance)
				};
			}

			public static ConfigurationAdaptiveStepParameters NativeToManaged(Representation representation)
			{
				ConfigurationAdaptiveStepParameters configurationAdaptiveStepParameters = new ConfigurationAdaptiveStepParameters();
				configurationAdaptiveStepParameters.adaptive_step_size_integrator = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.adaptive_step_size_integrator) as string;
				configurationAdaptiveStepParameters.length_integration_tolerance = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.length_integration_tolerance) as string;
				configurationAdaptiveStepParameters.speed_integration_tolerance = NoOwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.speed_integration_tolerance) as string;
				return configurationAdaptiveStepParameters;
			}

			public static ICustomMarshaler GetInstance(string s)
			{
				return instance_;
			}

			public override void CleanUpNativeDataImplementation(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.adaptive_step_size_integrator);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.length_integration_tolerance);
				NoOwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.speed_integration_tolerance);
				Marshal.FreeHGlobal(native_data);
			}

			public override IntPtr MarshalManagedToNativeImplementation(object managed_object)
			{
				if (!(managed_object is ConfigurationAdaptiveStepParameters value))
				{
					throw new NotSupportedException();
				}
				Representation structure = ManagedToNative(value);
				IntPtr intPtr = Marshal.AllocHGlobal(Marshal.SizeOf(structure));
				Marshal.StructureToPtr(structure, intPtr, fDeleteOld: false);
				return intPtr;
			}

			public override object MarshalNativeToManaged(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				return NativeToManaged(representation);
			}
		}

		public string adaptive_step_size_integrator;

		public string length_integration_tolerance;

		public string speed_integration_tolerance;
	}
	internal struct FlightPlanAdaptiveStepParameters
	{
		public long integrator_kind;

		public long generalized_integrator_kind;

		public long max_steps;

		public double length_integration_tolerance;

		public double speed_integration_tolerance;
	}
	internal partial struct Interval
	{
		public double min;

		public double max;

	}
	internal struct KeplerianElements
	{
		public double eccentricity;

		public double semimajor_axis;

		public double mean_motion;

		public double inclination_in_degrees;

		public double longitude_of_ascending_node_in_degrees;

		public double argument_of_periapsis_in_degrees;

		public double mean_anomaly;
	}
	internal class NavigationManoeuvre
	{
		internal class Marshaler : MonoMarshaler
		{
			internal struct Representation
			{
				public Burn.Marshaler.Representation burn;

				public double initial_mass_in_tonnes;

				public double final_mass_in_tonnes;

				public double mass_flow;

				public double duration;

				public double final_time;

				public double time_of_half_delta_v;

				public double time_to_half_delta_v;
			}

			private static readonly Marshaler instance_ = new Marshaler();

			public static Representation ManagedToNative(NavigationManoeuvre value)
			{
				return new Representation
				{
					burn = Burn.Marshaler.ManagedToNative(value.burn),
					initial_mass_in_tonnes = value.initial_mass_in_tonnes,
					final_mass_in_tonnes = value.final_mass_in_tonnes,
					mass_flow = value.mass_flow,
					duration = value.duration,
					final_time = value.final_time,
					time_of_half_delta_v = value.time_of_half_delta_v,
					time_to_half_delta_v = value.time_to_half_delta_v
				};
			}

			public static NavigationManoeuvre NativeToManaged(Representation representation)
			{
				return new NavigationManoeuvre
				{
					burn = Burn.Marshaler.NativeToManaged(representation.burn),
					initial_mass_in_tonnes = representation.initial_mass_in_tonnes,
					final_mass_in_tonnes = representation.final_mass_in_tonnes,
					mass_flow = representation.mass_flow,
					duration = representation.duration,
					final_time = representation.final_time,
					time_of_half_delta_v = representation.time_of_half_delta_v,
					time_to_half_delta_v = representation.time_to_half_delta_v
				};
			}

			public static ICustomMarshaler GetInstance(string s)
			{
				return instance_;
			}

			public override void CleanUpNativeDataImplementation(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				Marshal.FreeHGlobal(native_data);
			}

			public override IntPtr MarshalManagedToNativeImplementation(object managed_object)
			{
				if (!(managed_object is NavigationManoeuvre value))
				{
					throw new NotSupportedException();
				}
				Representation structure = ManagedToNative(value);
				IntPtr intPtr = Marshal.AllocHGlobal(Marshal.SizeOf(structure));
				Marshal.StructureToPtr(structure, intPtr, fDeleteOld: false);
				return intPtr;
			}

			public override object MarshalNativeToManaged(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				return NativeToManaged(representation);
			}
		}

		public Burn burn;

		public double initial_mass_in_tonnes;

		public double final_mass_in_tonnes;

		public double mass_flow;

		public double duration;

		public double final_time;

		public double time_of_half_delta_v;

		public double time_to_half_delta_v;
	}
	internal struct NavigationManoeuvreFrenetTrihedron
	{
		public XYZ binormal;

		public XYZ normal;

		public XYZ tangent;
	}
	internal struct Node
	{
		public double time;

		public XYZ world_position;

		public double apparent_inclination_in_degrees;

		public double out_of_plane_velocity;
	}
	internal struct Origin
	{
		private byte reference_part_is_at_origin_;

		private byte reference_part_is_unmoving_;

		public uint reference_part_id;

		public XYZ main_body_centre_in_world;

		public bool reference_part_is_at_origin
		{
			get
			{
				return reference_part_is_at_origin_ != 0;
			}
			set
			{
				reference_part_is_at_origin_ = (value ? ((byte)1) : ((byte)0));
			}
		}

		public bool reference_part_is_unmoving
		{
			get
			{
				return reference_part_is_unmoving_ != 0;
			}
			set
			{
				reference_part_is_unmoving_ = (value ? ((byte)1) : ((byte)0));
			}
		}
	}
	internal partial class PlottingFrameParameters : IReferenceFrameParameters, IConfigNode
	{
		internal class Marshaler : MonoMarshaler
		{
			internal struct Representation
			{
				public int extension;

				public int centre_index;

				public IntPtr primary_index;

				public IntPtr secondary_index;
			}

			private static readonly Marshaler instance_ = new Marshaler();

			public static Representation ManagedToNative(PlottingFrameParameters value)
			{
				return new Representation
				{
					extension = value.extension,
					centre_index = value.centre_index,
					primary_index = RepeatedMarshaler<int, OptionalMarshaler<int>>.GetInstance(null).MarshalManagedToNative(value.primary_index),
					secondary_index = RepeatedMarshaler<int, OptionalMarshaler<int>>.GetInstance(null).MarshalManagedToNative(value.secondary_index)
				};
			}

			public static PlottingFrameParameters NativeToManaged(Representation representation)
			{
				PlottingFrameParameters plottingFrameParameters = new PlottingFrameParameters();
				plottingFrameParameters.extension = representation.extension;
				plottingFrameParameters.centre_index = representation.centre_index;
				plottingFrameParameters.primary_index = RepeatedMarshaler<int, OptionalMarshaler<int>>.GetInstance(null).MarshalNativeToManaged(representation.primary_index) as int[];
				plottingFrameParameters.secondary_index = RepeatedMarshaler<int, OptionalMarshaler<int>>.GetInstance(null).MarshalNativeToManaged(representation.secondary_index) as int[];
				return plottingFrameParameters;
			}

			public static ICustomMarshaler GetInstance(string s)
			{
				return instance_;
			}

			public override void CleanUpNativeDataImplementation(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				RepeatedMarshaler<int, OptionalMarshaler<int>>.GetInstance(null).CleanUpNativeData(representation.primary_index);
				RepeatedMarshaler<int, OptionalMarshaler<int>>.GetInstance(null).CleanUpNativeData(representation.secondary_index);
				Marshal.FreeHGlobal(native_data);
			}

			public override IntPtr MarshalManagedToNativeImplementation(object managed_object)
			{
				if (!(managed_object is PlottingFrameParameters value))
				{
					throw new NotSupportedException();
				}
				Representation structure = ManagedToNative(value);
				IntPtr intPtr = Marshal.AllocHGlobal(Marshal.SizeOf(structure));
				Marshal.StructureToPtr(structure, intPtr, fDeleteOld: false);
				return intPtr;
			}

			public override object MarshalNativeToManaged(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				return NativeToManaged(representation);
			}
		}

		public int extension;

		public int centre_index;

		public int[] primary_index;

		public int[] secondary_index;

	}
	internal struct PlottingFramePayload
	{
		public Interval plottable_time_interval;
	}
	internal struct QP
	{
		public XYZ q;

		public XYZ p;
	}
	internal struct TQP
	{
		public double t;

		public QP qp;
	}
	internal partial class Status
	{
		internal class Marshaler : MonoMarshaler
		{
			internal struct Representation
			{
				public int error;

				public IntPtr message;
			}

			private static readonly Marshaler instance_ = new Marshaler();

			public static Representation ManagedToNative(Status value)
			{
				return new Representation
				{
					error = value.error,
					message = OwnershipTransferUTF8Marshaler.GetInstance(null).MarshalManagedToNative(value.message)
				};
			}

			public static Status NativeToManaged(Representation representation)
			{
				Status status = new Status();
				status.error = representation.error;
				status.message = OwnershipTransferUTF8Marshaler.GetInstance(null).MarshalNativeToManaged(representation.message) as string;
				return status;
			}

			public static ICustomMarshaler GetInstance(string s)
			{
				return instance_;
			}

			public override void CleanUpNativeDataImplementation(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				OwnershipTransferUTF8Marshaler.GetInstance(null).CleanUpNativeData(representation.message);
				Marshal.FreeHGlobal(native_data);
			}

			public override IntPtr MarshalManagedToNativeImplementation(object managed_object)
			{
				if (!(managed_object is Status value))
				{
					throw new NotSupportedException();
				}
				Representation structure = ManagedToNative(value);
				IntPtr intPtr = Marshal.AllocHGlobal(Marshal.SizeOf(structure));
				Marshal.StructureToPtr(structure, intPtr, fDeleteOld: false);
				return intPtr;
			}

			public override object MarshalNativeToManaged(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				return NativeToManaged(representation);
			}
		}

		public int error;

		public string message;

	}
	internal partial struct WXYZ
	{
		public double w;

		public double x;

		public double y;

		public double z;

	}
	internal struct QPRW
	{
		public QP qp;

		public WXYZ r;

		public XYZ w;
	}
	public struct XY
	{
		public double x;

		public double y;
	}
	internal struct PlottableElements
	{
		public double time;

		public double semimajor_axis;

		public double eccentricity;

		public double inclination;

		public double longitude_of_ascending_node;

		public double argument_of_periapsis;

		public double periapsis_distance;

		public double apoapsis_distance;

		public double lidov_c1;

		public double lidov_c2;

		public double eccentricity_cos_argument_of_periapsis;

		public double eccentricity_sin_argument_of_periapsis;
	}
	internal class OrbitalElements
	{
		internal class Marshaler : MonoMarshaler
		{
			internal struct Representation
			{
				public double sidereal_period;

				public double nodal_period;

				public double anomalistic_period;

				public double nodal_precession;

				public Interval mean_semimajor_axis;

				public Interval mean_eccentricity;

				public Interval mean_inclination;

				public Interval mean_longitude_of_ascending_nodes;

				public Interval mean_argument_of_periapsis;

				public Interval mean_periapsis_distance;

				public Interval mean_apoapsis_distance;

				public Interval radial_distance;

				public IntPtr first_collision_time;

				public IntPtr first_collision_risk_time;

				public IntPtr first_reentry_time;

				public IntPtr plottable_elements;
			}

			private static readonly Marshaler instance_ = new Marshaler();

			public static Representation ManagedToNative(OrbitalElements value)
			{
				return new Representation
				{
					sidereal_period = value.sidereal_period,
					nodal_period = value.nodal_period,
					anomalistic_period = value.anomalistic_period,
					nodal_precession = value.nodal_precession,
					mean_semimajor_axis = value.mean_semimajor_axis,
					mean_eccentricity = value.mean_eccentricity,
					mean_inclination = value.mean_inclination,
					mean_longitude_of_ascending_nodes = value.mean_longitude_of_ascending_nodes,
					mean_argument_of_periapsis = value.mean_argument_of_periapsis,
					mean_periapsis_distance = value.mean_periapsis_distance,
					mean_apoapsis_distance = value.mean_apoapsis_distance,
					radial_distance = value.radial_distance,
					first_collision_time = OwnershipTransferMarshaler<double, OptionalMarshaler<double>>.GetInstance(null).MarshalManagedToNative(value.first_collision_time),
					first_collision_risk_time = OwnershipTransferMarshaler<double, OptionalMarshaler<double>>.GetInstance(null).MarshalManagedToNative(value.first_collision_risk_time),
					first_reentry_time = OwnershipTransferMarshaler<double, OptionalMarshaler<double>>.GetInstance(null).MarshalManagedToNative(value.first_reentry_time),
					plottable_elements = DisposableIteratorMarshaler.GetInstance(null).MarshalManagedToNative(value.plottable_elements)
				};
			}

			public static OrbitalElements NativeToManaged(Representation representation)
			{
				OrbitalElements orbitalElements = new OrbitalElements();
				orbitalElements.sidereal_period = representation.sidereal_period;
				orbitalElements.nodal_period = representation.nodal_period;
				orbitalElements.anomalistic_period = representation.anomalistic_period;
				orbitalElements.nodal_precession = representation.nodal_precession;
				orbitalElements.mean_semimajor_axis = representation.mean_semimajor_axis;
				orbitalElements.mean_eccentricity = representation.mean_eccentricity;
				orbitalElements.mean_inclination = representation.mean_inclination;
				orbitalElements.mean_longitude_of_ascending_nodes = representation.mean_longitude_of_ascending_nodes;
				orbitalElements.mean_argument_of_periapsis = representation.mean_argument_of_periapsis;
				orbitalElements.mean_periapsis_distance = representation.mean_periapsis_distance;
				orbitalElements.mean_apoapsis_distance = representation.mean_apoapsis_distance;
				orbitalElements.radial_distance = representation.radial_distance;
				orbitalElements.first_collision_time = OwnershipTransferMarshaler<double, OptionalMarshaler<double>>.GetInstance(null).MarshalNativeToManaged(representation.first_collision_time) as double?;
				orbitalElements.first_collision_risk_time = OwnershipTransferMarshaler<double, OptionalMarshaler<double>>.GetInstance(null).MarshalNativeToManaged(representation.first_collision_risk_time) as double?;
				orbitalElements.first_reentry_time = OwnershipTransferMarshaler<double, OptionalMarshaler<double>>.GetInstance(null).MarshalNativeToManaged(representation.first_reentry_time) as double?;
				orbitalElements.plottable_elements = DisposableIteratorMarshaler.GetInstance(null).MarshalNativeToManaged(representation.plottable_elements) as DisposableIterator;
				return orbitalElements;
			}

			public static ICustomMarshaler GetInstance(string s)
			{
				return instance_;
			}

			public override void CleanUpNativeDataImplementation(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				OwnershipTransferMarshaler<double, OptionalMarshaler<double>>.GetInstance(null).CleanUpNativeData(representation.first_collision_time);
				OwnershipTransferMarshaler<double, OptionalMarshaler<double>>.GetInstance(null).CleanUpNativeData(representation.first_collision_risk_time);
				OwnershipTransferMarshaler<double, OptionalMarshaler<double>>.GetInstance(null).CleanUpNativeData(representation.first_reentry_time);
				DisposableIteratorMarshaler.GetInstance(null).CleanUpNativeData(representation.plottable_elements);
				Marshal.FreeHGlobal(native_data);
			}

			public override IntPtr MarshalManagedToNativeImplementation(object managed_object)
			{
				if (!(managed_object is OrbitalElements value))
				{
					throw new NotSupportedException();
				}
				Representation structure = ManagedToNative(value);
				IntPtr intPtr = Marshal.AllocHGlobal(Marshal.SizeOf(structure));
				Marshal.StructureToPtr(structure, intPtr, fDeleteOld: false);
				return intPtr;
			}

			public override object MarshalNativeToManaged(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				return NativeToManaged(representation);
			}
		}

		public double sidereal_period;

		public double nodal_period;

		public double anomalistic_period;

		public double nodal_precession;

		public Interval mean_semimajor_axis;

		public Interval mean_eccentricity;

		public Interval mean_inclination;

		public Interval mean_longitude_of_ascending_nodes;

		public Interval mean_argument_of_periapsis;

		public Interval mean_periapsis_distance;

		public Interval mean_apoapsis_distance;

		public Interval radial_distance;

		public double? first_collision_time;

		public double? first_collision_risk_time;

		public double? first_reentry_time;

		public DisposableIterator plottable_elements;
	}
	internal struct OrbitRecurrence
	{
		public int nuo;

		public int dto;

		public int cto;

		public int number_of_revolutions;

		public double equatorial_shift;

		public double base_interval;

		public double grid_interval;

		public int subcycle;
	}
	internal struct EquatorialCrossings
	{
		public Interval longitudes_reduced_to_ascending_pass;

		public Interval longitudes_reduced_to_descending_pass;
	}
	internal struct SolarTimesOfNodes
	{
		public Interval mean_solar_times_of_ascending_nodes;

		public Interval mean_solar_times_of_descending_nodes;
	}
	internal class OrbitAnalysis
	{
		internal class Marshaler : MonoMarshaler
		{
			internal struct Representation
			{
				public double progress_of_next_analysis;

				public IntPtr primary_index;

				public double mission_duration;

				public IntPtr elements;

				public IntPtr recurrence;

				public IntPtr ground_track_equatorial_crossings;

				public IntPtr solar_times_of_nodes;
			}

			private static readonly Marshaler instance_ = new Marshaler();

			public static Representation ManagedToNative(OrbitAnalysis value)
			{
				return new Representation
				{
					progress_of_next_analysis = value.progress_of_next_analysis,
					primary_index = OwnershipTransferMarshaler<int, OptionalMarshaler<int>>.GetInstance(null).MarshalManagedToNative(value.primary_index),
					mission_duration = value.mission_duration,
					elements = OwnershipTransferMarshaler<OrbitalElements, OptionalMarshaler<OrbitalElements, OrbitalElements.Marshaler>>.GetInstance(null).MarshalManagedToNative(value.elements),
					recurrence = OwnershipTransferMarshaler<OrbitRecurrence, OptionalMarshaler<OrbitRecurrence>>.GetInstance(null).MarshalManagedToNative(value.recurrence),
					ground_track_equatorial_crossings = OwnershipTransferMarshaler<EquatorialCrossings, OptionalMarshaler<EquatorialCrossings>>.GetInstance(null).MarshalManagedToNative(value.ground_track_equatorial_crossings),
					solar_times_of_nodes = OwnershipTransferMarshaler<SolarTimesOfNodes, OptionalMarshaler<SolarTimesOfNodes>>.GetInstance(null).MarshalManagedToNative(value.solar_times_of_nodes)
				};
			}

			public static OrbitAnalysis NativeToManaged(Representation representation)
			{
				OrbitAnalysis orbitAnalysis = new OrbitAnalysis();
				orbitAnalysis.progress_of_next_analysis = representation.progress_of_next_analysis;
				orbitAnalysis.primary_index = OwnershipTransferMarshaler<int, OptionalMarshaler<int>>.GetInstance(null).MarshalNativeToManaged(representation.primary_index) as int?;
				orbitAnalysis.mission_duration = representation.mission_duration;
				orbitAnalysis.elements = OwnershipTransferMarshaler<OrbitalElements, OptionalMarshaler<OrbitalElements, OrbitalElements.Marshaler>>.GetInstance(null).MarshalNativeToManaged(representation.elements) as OrbitalElements;
				orbitAnalysis.recurrence = OwnershipTransferMarshaler<OrbitRecurrence, OptionalMarshaler<OrbitRecurrence>>.GetInstance(null).MarshalNativeToManaged(representation.recurrence) as OrbitRecurrence?;
				orbitAnalysis.ground_track_equatorial_crossings = OwnershipTransferMarshaler<EquatorialCrossings, OptionalMarshaler<EquatorialCrossings>>.GetInstance(null).MarshalNativeToManaged(representation.ground_track_equatorial_crossings) as EquatorialCrossings?;
				orbitAnalysis.solar_times_of_nodes = OwnershipTransferMarshaler<SolarTimesOfNodes, OptionalMarshaler<SolarTimesOfNodes>>.GetInstance(null).MarshalNativeToManaged(representation.solar_times_of_nodes) as SolarTimesOfNodes?;
				return orbitAnalysis;
			}

			public static ICustomMarshaler GetInstance(string s)
			{
				return instance_;
			}

			public override void CleanUpNativeDataImplementation(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				OwnershipTransferMarshaler<int, OptionalMarshaler<int>>.GetInstance(null).CleanUpNativeData(representation.primary_index);
				OwnershipTransferMarshaler<OrbitalElements, OptionalMarshaler<OrbitalElements, OrbitalElements.Marshaler>>.GetInstance(null).CleanUpNativeData(representation.elements);
				OwnershipTransferMarshaler<OrbitRecurrence, OptionalMarshaler<OrbitRecurrence>>.GetInstance(null).CleanUpNativeData(representation.recurrence);
				OwnershipTransferMarshaler<EquatorialCrossings, OptionalMarshaler<EquatorialCrossings>>.GetInstance(null).CleanUpNativeData(representation.ground_track_equatorial_crossings);
				OwnershipTransferMarshaler<SolarTimesOfNodes, OptionalMarshaler<SolarTimesOfNodes>>.GetInstance(null).CleanUpNativeData(representation.solar_times_of_nodes);
				Marshal.FreeHGlobal(native_data);
			}

			public override IntPtr MarshalManagedToNativeImplementation(object managed_object)
			{
				if (!(managed_object is OrbitAnalysis value))
				{
					throw new NotSupportedException();
				}
				Representation structure = ManagedToNative(value);
				IntPtr intPtr = Marshal.AllocHGlobal(Marshal.SizeOf(structure));
				Marshal.StructureToPtr(structure, intPtr, fDeleteOld: false);
				return intPtr;
			}

			public override object MarshalNativeToManaged(IntPtr native_data)
			{
				Representation representation = (Representation)Marshal.PtrToStructure(native_data, typeof(Representation));
				return NativeToManaged(representation);
			}
		}

		public double progress_of_next_analysis;

		public int? primary_index;

		public double mission_duration;

		public OrbitalElements elements;

		public OrbitRecurrence? recurrence;

		public EquatorialCrossings? ground_track_equatorial_crossings;

		public SolarTimesOfNodes? solar_times_of_nodes;
	}
	internal static partial class Interface
	{
		private class Symbols
		{
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void ActivatePlayerDelegate();

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void ActivateRecorderDelegate([MarshalAs(UnmanagedType.I1)] bool activate);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void AdvanceTimeDelegate(IntPtr plugin, double t, double planetarium_rotation);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate WXYZ CameraReferenceRotationDelegate(IntPtr plugin);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate double CameraScaleDelegate(IntPtr plugin);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void CatchUpLaggingVesselsDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] out DisposableIterator collided_vessels);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate QP CelestialFromParentDelegate(IntPtr plugin, int celestial_index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")]
			public delegate DisposableIterator CelestialGetAllNamesDelegate(IntPtr plugin);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate double CelestialInitialRotationInDegreesDelegate(IntPtr plugin, int celestial_index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate WXYZ CelestialRotationDelegate(IntPtr plugin, int index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate double CelestialRotationPeriodDelegate(IntPtr plugin, int celestial_index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate WXYZ CelestialSphereRotationDelegate(IntPtr plugin);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate QP CelestialWorldDegreesOfFreedomDelegate(IntPtr plugin, int index, Origin origin, double time);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.I1)]
			public delegate bool CollisionDeleteExecutorDelegate(IntPtr plugin, ref IntPtr executor, out TQP collision);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.I1)]
			public delegate bool CollisionGetLatitudeLongitudeDelegate(IntPtr executor, out double latitude_in_degrees, out double longitude_in_degrees);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate IntPtr CollisionNewFlightPlanExecutorDelegate(IntPtr plugin, int celestial_index, XYZ sun_world_position, int max_points, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate IntPtr CollisionNewPredictionExecutorDelegate(IntPtr plugin, int celestial_index, XYZ sun_world_position, int max_points, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void CollisionSetRadiusDelegate(IntPtr executor, double radius);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void ClearFlagsDelegate();

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void ClearTargetVesselDelegate(IntPtr plugin);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void ClearWorldRotationalReferenceFrameDelegate(IntPtr plugin);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate double CurrentTimeDelegate(IntPtr plugin);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void DeleteInterchangeDelegate(ref IntPtr native_pointer);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void DeletePluginDelegate(ref IntPtr plugin);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void DeleteStringDelegate(ref IntPtr native_string);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void DeleteU16StringDelegate(ref IntPtr native_string);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void DeserializePluginDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string serialization, ref IntPtr deserializer, ref IntPtr plugin_reader, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string compressor, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string encoder);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void EndInitializationDelegate(IntPtr plugin);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate int EquipotentialCountDelegate(IntPtr plugin);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate int FlightPlanCountDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void FlightPlanCreateDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, double final_time, double mass_in_tonnes);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void FlightPlanDeleteDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void FlightPlanDuplicateDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.I1)]
			public delegate bool FlightPlanExistsDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate FlightPlanAdaptiveStepParameters FlightPlanGetAdaptiveStepParametersDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate double FlightPlanGetActualFinalTimeDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OwnershipTransferMarshaler`2[principia.ksp_plugin_adapter.Status,principia.ksp_plugin_adapter.Status+Marshaler]")]
			public delegate Status FlightPlanGetAnomalousStatusDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OwnershipTransferMarshaler`2[principia.ksp_plugin_adapter.OrbitAnalysis,principia.ksp_plugin_adapter.OrbitAnalysis+Marshaler]")]
			public delegate OrbitAnalysis FlightPlanGetCoastAnalysisDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OptionalMarshaler`1[[System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]")] BoxedInt32 revolutions_per_cycle, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OptionalMarshaler`1[[System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]")] BoxedInt32 days_per_cycle, int ground_track_revolution, int index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate double FlightPlanGetDesiredFinalTimeDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate XYZ FlightPlanGetGuidanceDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, int index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate double FlightPlanGetInitialTimeDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OwnershipTransferMarshaler`2[principia.ksp_plugin_adapter.NavigationManoeuvre,principia.ksp_plugin_adapter.NavigationManoeuvre+Marshaler]")]
			public delegate NavigationManoeuvre FlightPlanGetManoeuvreDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, int index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate NavigationManoeuvreFrenetTrihedron FlightPlanGetManoeuvreFrenetTrihedronDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, int index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate XYZ FlightPlanGetManoeuvreInitialPlottedVelocityDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, int index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OwnershipTransferMarshaler`2[principia.ksp_plugin_adapter.Status,principia.ksp_plugin_adapter.Status+Marshaler]")]
			public delegate Status FlightPlanInsertDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.Burn+Marshaler")] Burn burn, int index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate int FlightPlanNumberOfAnomalousManoeuvresDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate int FlightPlanNumberOfManoeuvresDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate int FlightPlanNumberOfSegmentsDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate int FlightPlanOptimizationDriverInProgressDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void FlightPlanOptimizationDriverMakeDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, double distance, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OptionalMarshaler`1[[System.Double, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]")] BoxedDouble inclination_in_degrees, int celestial_index, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NavigationFrameParameters+Marshaler")] NavigationFrameParameters navigation_frame_parameters);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void FlightPlanOptimizationDriverStartDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, int manoeuvre_index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OwnershipTransferMarshaler`2[principia.ksp_plugin_adapter.Status,principia.ksp_plugin_adapter.Status+Marshaler]")]
			public delegate Status FlightPlanRebaseDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, double mass_in_tonnes);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OwnershipTransferMarshaler`2[principia.ksp_plugin_adapter.Status,principia.ksp_plugin_adapter.Status+Marshaler]")]
			public delegate Status FlightPlanRemoveDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, int index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void FlightPlanRenderedApsidesDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OptionalMarshaler`1[[System.Double, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]")] BoxedDouble t_max, int celestial_index, XYZ sun_world_position, int max_points, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] out DisposableIterator apoapsides, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] out DisposableIterator periapsides);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void FlightPlanRenderedClosestApproachesDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, XYZ sun_world_position, int max_points, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] out DisposableIterator closest_approaches);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")]
			public delegate DisposableIterator FlightPlanRenderedManoeuvreInitialDegreesOfFreedomDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, XYZ sun_world_position, int index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void FlightPlanRenderedNodesDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OptionalMarshaler`1[[System.Double, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]")] BoxedDouble t_max, XYZ sun_world_position, int max_points, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] out DisposableIterator ascending, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] out DisposableIterator descending);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OwnershipTransferMarshaler`2[principia.ksp_plugin_adapter.Status,principia.ksp_plugin_adapter.Status+Marshaler]")]
			public delegate Status FlightPlanReplaceDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.Burn+Marshaler")] Burn burn, int index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void FlightPlanSelectDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, int index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate int FlightPlanSelectedDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OwnershipTransferMarshaler`2[principia.ksp_plugin_adapter.Status,principia.ksp_plugin_adapter.Status+Marshaler]")]
			public delegate Status FlightPlanSetAdaptiveStepParametersDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, FlightPlanAdaptiveStepParameters flight_plan_adaptive_step_parameters);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OwnershipTransferMarshaler`2[principia.ksp_plugin_adapter.Status,principia.ksp_plugin_adapter.Status+Marshaler]")]
			public delegate Status FlightPlanSetDesiredFinalTimeDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, double final_time);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.I1)]
			public delegate bool FlightPlanUpdateFromOptimizationDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void FreeVesselsAndPartsAndCollectPileUpsDelegate(IntPtr plugin, double delta_t);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate IntPtr FutureCatchUpVesselDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.I1)]
			public delegate bool FutureTryHarvestVesselCatchUpDelegate(IntPtr plugin, ref IntPtr future, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] out DisposableIterator collided_vessels);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void FutureWaitForVesselToCatchUpDelegate(IntPtr plugin, ref IntPtr future, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] out DisposableIterator collided_vessels);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate int GetBufferedLoggingDelegate();

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void GetCPUIDFeatureFlagsDelegate([MarshalAs(UnmanagedType.I1)] out bool has_avx, [MarshalAs(UnmanagedType.I1)] out bool has_fma);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate int GetStderrLoggingDelegate();

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate int GetSuppressedLoggingDelegate();

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate int GetVerboseLoggingDelegate();

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void GetVersionDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] out string build_date, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] out string version, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] out string platform);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate double GraphLidovFrozenLineDelegate(double c2);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate double GraphLidovMaximalEccentricityLineDelegate(double eccentricity, double c2);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate Interval GraphLidovMaximalEccentricityLineC2RangeDelegate(double eccentricity);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate double GraphLidovMaximalInclinationLineDelegate(double inclination_in_degrees, double c2);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate Interval GraphLidovMaximalInclinationLineC2RangeDelegate(double inclination_in_degrees);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate double GraphLidovMinimalEccentricityLeftLineDelegate(double eccentricity, double c2);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate Interval GraphLidovMinimalEccentricityLeftLineC2RangeDelegate(double eccentricity);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void GraphLidovMinimalEccentricityRightLineC2AndC1MaxDelegate(double eccentricity, out double c2, out double c1_max);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate double GraphLidovMinimalInclinationLineDelegate(double inclination_in_degrees, double c2);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate Interval GraphLidovMinimalInclinationLineC2RangeDelegate(double inclination_in_degrees);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.I1)]
			public delegate bool HasEncounteredApocalypseDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OwnershipTransferUTF8Marshaler")] out string details);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.I1)]
			public delegate bool HasVesselDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void InitGoogleLoggingDelegate();

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void InitializeDownsamplingParametersDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.ConfigurationDownsamplingParameters+Marshaler")] ConfigurationDownsamplingParameters downsampling_parameters);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void InitializeEphemerisParametersDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.ConfigurationAccuracyParameters+Marshaler")] ConfigurationAccuracyParameters accuracy_parameters, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.ConfigurationFixedStepParameters+Marshaler")] ConfigurationFixedStepParameters fixed_step_parameters);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void InitializeHistoryParametersDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.ConfigurationFixedStepParameters+Marshaler")] ConfigurationFixedStepParameters fixed_step_parameters);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void InitializePsychohistoryParametersDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.ConfigurationAdaptiveStepParameters+Marshaler")] ConfigurationAdaptiveStepParameters parameters);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void InsertCelestialAbsoluteCartesianDelegate(IntPtr plugin, int celestial_index, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OptionalMarshaler`1[[System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]")] BoxedInt32 parent_index, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.BodyParameters+Marshaler")] BodyParameters body_parameters, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string x, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string y, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string z, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vx, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vy, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vz);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void InsertCelestialJacobiKeplerianDelegate(IntPtr plugin, int celestial_index, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OptionalMarshaler`1[[System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]")] BoxedInt32 parent_index, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.BodyParameters+Marshaler")] BodyParameters body_parameters, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OptionalMarshaler`1[principia.ksp_plugin_adapter.KeplerianElements]")] BoxedKeplerianElements keplerian_elements);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void InsertOrKeepVesselDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_name, int parent_index, [MarshalAs(UnmanagedType.I1)] bool loaded, [MarshalAs(UnmanagedType.I1)] out bool inserted);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void InsertOrKeepLoadedPartDelegate(IntPtr plugin, uint part_id, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string name, double mass_in_tonnes, XYZ centre_of_mass, XYZ moments_of_inertia_in_tonnes, WXYZ principal_axes_rotation, [MarshalAs(UnmanagedType.I1)] bool is_solid_rocket_motor, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, int main_body_index, QP main_body_world_degrees_of_freedom, QP part_world_degrees_of_freedom, WXYZ part_rotation, XYZ part_angular_velocity, double delta_t);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void InsertUnloadedPartDelegate(IntPtr plugin, uint part_id, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string name, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, QP from_parent);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.I1)]
			public delegate bool IteratorAtEndDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] DisposableIterator iterator);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void IteratorDeleteDelegate(ref IntPtr iterator);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")]
			public delegate string IteratorGetCelestialNameDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] DisposableIterator iterator);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate QP IteratorGetDiscreteTrajectoryQPDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] DisposableIterator iterator);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate double IteratorGetDiscreteTrajectoryTimeDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] DisposableIterator iterator);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate XYZ IteratorGetDiscreteTrajectoryXYZDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] DisposableIterator iterator);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate QP IteratorGetDistinguishedPointsQPDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] DisposableIterator iterator);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate double IteratorGetDistinguishedPointsTimeDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] DisposableIterator iterator);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate XYZ IteratorGetDistinguishedPointsXYZDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] DisposableIterator iterator);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate Node IteratorGetNodeDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] DisposableIterator iterator);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")]
			public delegate DisposableIterator IteratorGetRP2LinesIteratorDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] DisposableIterator iterator);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate XY IteratorGetRP2LineXYDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] DisposableIterator iterator);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")]
			public delegate string IteratorGetVesselGuidDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] DisposableIterator iterator);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate PlottableElements IteratorGetPlottableElementsDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] DisposableIterator iterator);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void IteratorIncrementDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] DisposableIterator iterator);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void IteratorResetDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] DisposableIterator iterator);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate int IteratorSizeDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] DisposableIterator iterator);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void LogErrorDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string file, int line, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string text);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void LogFatalDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string file, int line, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string text);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void LogInfoDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string file, int line, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string text);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void LogWarningDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string file, int line, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string text);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void MonitorSetNameDelegate(int i, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string name);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void MonitorStartDelegate(int i);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void MonitorStopDelegate(int i);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate WXYZ NavballOrientationDelegate(IntPtr plugin, XYZ sun_world_position, XYZ ship_world_position);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate IntPtr NewPluginDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string game_epoch, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string solar_system_epoch, double planetarium_rotation_in_degrees);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate XYZ PartAngularMomentumFromAngularVelocityDelegate(XYZ world_angular_velocity, XYZ moments_of_inertia_in_tonnes, WXYZ principal_axes_rotation, WXYZ part_rotation);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void PartApplyIntrinsicForceDelegate(IntPtr plugin, uint part_id, XYZ force_in_kilonewtons);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void PartApplyIntrinsicForceAtPositionDelegate(IntPtr plugin, uint part_id, XYZ force_in_kilonewtons, XYZ lever_arm);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void PartApplyIntrinsicTorqueDelegate(IntPtr plugin, uint part_id, XYZ torque_in_kilonewton_metre);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate XYZ PartDragTorqueFromAngularVelocityDelegate(double angular_drag_per_second, double delta_t, XYZ world_angular_velocity, XYZ moments_of_inertia_in_tonnes, WXYZ principal_axes_rotation, WXYZ part_rotation);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate QPRW PartGetActualRigidMotionDelegate(IntPtr plugin, uint part_id, Origin origin);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.I1)]
			public delegate bool PartIsTruthfulDelegate(IntPtr plugin, uint part_id);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void PartSetApparentRigidMotionDelegate(IntPtr plugin, uint part_id, QP degrees_of_freedom, WXYZ rotation, XYZ angular_velocity);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposablePlanetariumMarshaler")]
			public delegate DisposablePlanetarium PlanetariumCreateDelegate(IntPtr plugin, XYZ sun_world_position, XYZ xyz_opengl_camera_x_in_world, XYZ xyz_opengl_camera_y_in_world, XYZ xyz_opengl_camera_z_in_world, XYZ xyz_camera_position_in_world, double focal, double field_of_view, double inverse_scale_factor, double angular_resolution, XYZ scaled_space_origin);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void PlanetariumDeleteDelegate(ref IntPtr planetarium);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void PlanetariumPlotCelestialFutureTrajectoryDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposablePlanetariumMarshaler")] DisposablePlanetarium planetarium, IntPtr plugin, int celestial_index, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, IntPtr vertices, int vertices_size, out double minimal_distance_from_camera, out int vertex_count);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void PlanetariumPlotCelestialPastTrajectoryDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposablePlanetariumMarshaler")] DisposablePlanetarium planetarium, IntPtr plugin, int celestial_index, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, double max_history_length, IntPtr vertices, int vertices_size, out double minimal_distance_from_camera, out int vertex_count);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void PlanetariumPlotEquipotentialDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposablePlanetariumMarshaler")] DisposablePlanetarium planetarium, IntPtr plugin, int index, IntPtr vertices, int vertices_size, out int vertex_count);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void PlanetariumPlotFlightPlanSegmentDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposablePlanetariumMarshaler")] DisposablePlanetarium planetarium, IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, int index, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OptionalMarshaler`1[[System.Double, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]")] BoxedDouble t_max, IntPtr vertices, int vertices_size, out int vertex_count);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void PlanetariumPlotFlightPlanSegmentDirectDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposablePlanetariumMarshaler")] DisposablePlanetarium planetarium, IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, int segment_index, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OptionalMarshaler`1[[System.Double, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]")] BoxedDouble t_max, IntPtr buffer, int buffer_size, [MarshalAs(UnmanagedType.I1)] bool is_d3d11_buffer, out int vertex_count);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void PlanetariumPlotPredictionDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposablePlanetariumMarshaler")] DisposablePlanetarium planetarium, IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OptionalMarshaler`1[[System.Double, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]")] BoxedDouble t_max, IntPtr vertices, int vertices_size, out int vertex_count);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void PlanetariumPlotPredictionDirectDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposablePlanetariumMarshaler")] DisposablePlanetarium planetarium, IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OptionalMarshaler`1[[System.Double, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]")] BoxedDouble t_max, IntPtr buffer, int buffer_size, [MarshalAs(UnmanagedType.I1)] bool is_d3d11_buffer, out int vertex_count);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void PlanetariumPlotPsychohistoryDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposablePlanetariumMarshaler")] DisposablePlanetarium planetarium, IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, double max_history_length, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OptionalMarshaler`1[[System.Double, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]")] BoxedDouble t_max, IntPtr vertices, int vertices_size, out int vertex_count);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void PlanetariumPlotPsychohistoryDirectDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposablePlanetariumMarshaler")] DisposablePlanetarium planetarium, IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, double max_history_length, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OptionalMarshaler`1[[System.Double, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]")] BoxedDouble t_max, IntPtr buffer, int buffer_size, [MarshalAs(UnmanagedType.I1)] bool is_d3d11_buffer, out int vertex_count);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate IntPtr PluginReaderAwaitDelegate(ref IntPtr reader);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate IntPtr PluginReaderGetDelegate(ref IntPtr reader);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")]
			public delegate string PluginReaderLogsDelegate(IntPtr reader);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.I1)]
			public delegate bool PluginReaderWillBeSlowDelegate(IntPtr reader);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void PrepareToReportCollisionsDelegate(IntPtr plugin);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void RenderedPredictionApsidesDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OptionalMarshaler`1[[System.Double, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]")] BoxedDouble t_max, int celestial_index, XYZ sun_world_position, int max_points, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] out DisposableIterator apoapsides, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] out DisposableIterator periapsides);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void RenderedPredictionClosestApproachesDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, XYZ sun_world_position, int max_points, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] out DisposableIterator closest_approaches);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void RenderedPredictionNodesDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OptionalMarshaler`1[[System.Double, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]")] BoxedDouble t_max, XYZ sun_world_position, int max_points, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] out DisposableIterator ascending, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.DisposableIteratorMarshaler")] out DisposableIterator descending);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void ReportGroundCollisionDelegate(IntPtr plugin, uint part_id);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void ReportPartCollisionDelegate(IntPtr plugin, uint part1_id, uint part2_id);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")]
			public delegate string SayHelloDelegate();

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OwnershipTransferMarshaler`2[principia.ksp_plugin_adapter.Status,principia.ksp_plugin_adapter.Status+Marshaler]")]
			public delegate Status SayNotFoundDelegate();

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OwnershipTransferUTF8Marshaler")]
			public delegate string SerializePluginDelegate(IntPtr plugin, ref IntPtr serializer, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string compressor, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string encoder);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void SetBufferedLoggingDelegate(int max_severity);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void SetFlagDelegate([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string name, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string value);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void SetMainBodyDelegate(IntPtr plugin, int index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void SetPlottingFrameDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.PlottingFrameParameters+Marshaler")] PlottingFrameParameters parameters);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void SetStderrLoggingDelegate(int min_severity);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void SetSuppressedLoggingDelegate(int min_severity);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void SetTargetVesselDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, int reference_body_index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void SetVerboseLoggingDelegate(int level);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void SetWorldRotationalReferenceFrameDelegate(IntPtr plugin, int index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate XYZ UnmanageableVesselVelocityDelegate(IntPtr plugin, QP degrees_of_freedom, int celestial_index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void UpdateCelestialHierarchyDelegate(IntPtr plugin, int celestial_index, int parent_index);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void UpdatePredictionDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.RepeatedMarshaler`2[[System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089],principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler]")] string[] vessel_guids);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate XYZ VesselBinormalDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void VesselClearPlottingFramePayloadDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.PlottingFrameParameters+Marshaler")] PlottingFrameParameters parameters);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate QP VesselFromParentDelegate(IntPtr plugin, int parent_index, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OwnershipTransferMarshaler`2[principia.ksp_plugin_adapter.OrbitAnalysis,principia.ksp_plugin_adapter.OrbitAnalysis+Marshaler]")]
			public delegate OrbitAnalysis VesselGetAnalysisDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OptionalMarshaler`1[[System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]")] BoxedInt32 revolutions_per_cycle, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.OptionalMarshaler`1[[System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]")] BoxedInt32 days_per_cycle, int ground_track_revolution);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate PlottingFramePayload VesselGetPlottingFramePayloadDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.PlottingFrameParameters+Marshaler")] PlottingFrameParameters parameters);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate AdaptiveStepParameters VesselGetPredictionAdaptiveStepParametersDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate XYZ VesselNormalDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void VesselRequestAnalysisDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, double mission_duration);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void VesselSetPlottingFramePayloadDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.PlottingFrameParameters+Marshaler")] PlottingFrameParameters parameters, PlottingFramePayload payload);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void VesselSetPredictionAdaptiveStepParametersDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid, AdaptiveStepParameters adaptive_step_parameters);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate XYZ VesselTangentDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate XYZ VesselVelocityDelegate(IntPtr plugin, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = "principia.ksp_plugin_adapter.NoOwnershipTransferUTF8Marshaler")] string vessel_guid);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate IntPtr StateBusCreateChannelDelegate();

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void StateBusDestroyChannelDelegate(ref IntPtr channel);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void StateBusCommitWriteDelegate(IntPtr channel, [In] ref DensePolynomialSlice slice);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.I1)]
			public delegate bool StateBusAcquireReadDelegate(IntPtr channel, out DensePolynomialSlice out_slice);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate IntPtr CommandBusCreateDelegate();

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void CommandBusDestroyDelegate(ref IntPtr queue);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.I1)]
			public delegate bool CommandBusTryPushDelegate(IntPtr queue, [In] ref CommandPacket packet);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.I1)]
			public delegate bool CommandBusTryPopDelegate(IntPtr queue, out CommandPacket out_packet);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void BatchInsertOrKeepLoadedPartsDelegate(
				IntPtr plugin,
				[In] PartBatchInsertState[] states,
				int count);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void BatchPartSetApparentRigidMotionDelegate(
				IntPtr plugin,
				[In] PartBatchApparentMotion[] states,
				int count);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			public delegate void BatchPartGetActualRigidMotionDelegate(
				IntPtr plugin,
				[In] ref Origin origin,
				[In] uint[] part_ids,
				[Out] QPRW[] motions_out,
				int count);

			public ActivatePlayerDelegate ActivatePlayer = Loader.LoadFunction<ActivatePlayerDelegate>("principia__ActivatePlayer");

			public ActivateRecorderDelegate ActivateRecorder = Loader.LoadFunction<ActivateRecorderDelegate>("principia__ActivateRecorder");

			public AdvanceTimeDelegate AdvanceTime = Loader.LoadFunction<AdvanceTimeDelegate>("principia__AdvanceTime");

			public CameraReferenceRotationDelegate CameraReferenceRotation = Loader.LoadFunction<CameraReferenceRotationDelegate>("principia__CameraReferenceRotation");

			public CameraScaleDelegate CameraScale = Loader.LoadFunction<CameraScaleDelegate>("principia__CameraScale");

			public CatchUpLaggingVesselsDelegate CatchUpLaggingVessels = Loader.LoadFunction<CatchUpLaggingVesselsDelegate>("principia__CatchUpLaggingVessels");

			public CelestialFromParentDelegate CelestialFromParent = Loader.LoadFunction<CelestialFromParentDelegate>("principia__CelestialFromParent");

			public CelestialGetAllNamesDelegate CelestialGetAllNames = Loader.LoadFunction<CelestialGetAllNamesDelegate>("principia__CelestialGetAllNames");

			public CelestialInitialRotationInDegreesDelegate CelestialInitialRotationInDegrees = Loader.LoadFunction<CelestialInitialRotationInDegreesDelegate>("principia__CelestialInitialRotationInDegrees");

			public CelestialRotationDelegate CelestialRotation = Loader.LoadFunction<CelestialRotationDelegate>("principia__CelestialRotation");

			public CelestialRotationPeriodDelegate CelestialRotationPeriod = Loader.LoadFunction<CelestialRotationPeriodDelegate>("principia__CelestialRotationPeriod");

			public CelestialSphereRotationDelegate CelestialSphereRotation = Loader.LoadFunction<CelestialSphereRotationDelegate>("principia__CelestialSphereRotation");

			public CelestialWorldDegreesOfFreedomDelegate CelestialWorldDegreesOfFreedom = Loader.LoadFunction<CelestialWorldDegreesOfFreedomDelegate>("principia__CelestialWorldDegreesOfFreedom");

			public CollisionDeleteExecutorDelegate CollisionDeleteExecutor = Loader.LoadFunction<CollisionDeleteExecutorDelegate>("principia__CollisionDeleteExecutor");

			public CollisionGetLatitudeLongitudeDelegate CollisionGetLatitudeLongitude = Loader.LoadFunction<CollisionGetLatitudeLongitudeDelegate>("principia__CollisionGetLatitudeLongitude");

			public CollisionNewFlightPlanExecutorDelegate CollisionNewFlightPlanExecutor = Loader.LoadFunction<CollisionNewFlightPlanExecutorDelegate>("principia__CollisionNewFlightPlanExecutor");

			public CollisionNewPredictionExecutorDelegate CollisionNewPredictionExecutor = Loader.LoadFunction<CollisionNewPredictionExecutorDelegate>("principia__CollisionNewPredictionExecutor");

			public CollisionSetRadiusDelegate CollisionSetRadius = Loader.LoadFunction<CollisionSetRadiusDelegate>("principia__CollisionSetRadius");

			public ClearFlagsDelegate ClearFlags = Loader.LoadFunction<ClearFlagsDelegate>("principia__ClearFlags");

			public ClearTargetVesselDelegate ClearTargetVessel = Loader.LoadFunction<ClearTargetVesselDelegate>("principia__ClearTargetVessel");

			public ClearWorldRotationalReferenceFrameDelegate ClearWorldRotationalReferenceFrame = Loader.LoadFunction<ClearWorldRotationalReferenceFrameDelegate>("principia__ClearWorldRotationalReferenceFrame");

			public CurrentTimeDelegate CurrentTime = Loader.LoadFunction<CurrentTimeDelegate>("principia__CurrentTime");

			public DeleteInterchangeDelegate DeleteInterchange = Loader.LoadFunction<DeleteInterchangeDelegate>("principia__DeleteInterchange");

			public DeletePluginDelegate DeletePlugin = Loader.LoadFunction<DeletePluginDelegate>("principia__DeletePlugin");

			public DeleteStringDelegate DeleteString = Loader.LoadFunction<DeleteStringDelegate>("principia__DeleteString");

			public DeleteU16StringDelegate DeleteU16String = Loader.LoadFunction<DeleteU16StringDelegate>("principia__DeleteU16String");

			public DeserializePluginDelegate DeserializePlugin = Loader.LoadFunction<DeserializePluginDelegate>("principia__DeserializePlugin");

			public EndInitializationDelegate EndInitialization = Loader.LoadFunction<EndInitializationDelegate>("principia__EndInitialization");

			public EquipotentialCountDelegate EquipotentialCount = Loader.LoadFunction<EquipotentialCountDelegate>("principia__EquipotentialCount");

			public FlightPlanCountDelegate FlightPlanCount = Loader.LoadFunction<FlightPlanCountDelegate>("principia__FlightPlanCount");

			public FlightPlanCreateDelegate FlightPlanCreate = Loader.LoadFunction<FlightPlanCreateDelegate>("principia__FlightPlanCreate");

			public FlightPlanDeleteDelegate FlightPlanDelete = Loader.LoadFunction<FlightPlanDeleteDelegate>("principia__FlightPlanDelete");

			public FlightPlanDuplicateDelegate FlightPlanDuplicate = Loader.LoadFunction<FlightPlanDuplicateDelegate>("principia__FlightPlanDuplicate");

			public FlightPlanExistsDelegate FlightPlanExists = Loader.LoadFunction<FlightPlanExistsDelegate>("principia__FlightPlanExists");

			public FlightPlanGetAdaptiveStepParametersDelegate FlightPlanGetAdaptiveStepParameters = Loader.LoadFunction<FlightPlanGetAdaptiveStepParametersDelegate>("principia__FlightPlanGetAdaptiveStepParameters");

			public FlightPlanGetActualFinalTimeDelegate FlightPlanGetActualFinalTime = Loader.LoadFunction<FlightPlanGetActualFinalTimeDelegate>("principia__FlightPlanGetActualFinalTime");

			public FlightPlanGetAnomalousStatusDelegate FlightPlanGetAnomalousStatus = Loader.LoadFunction<FlightPlanGetAnomalousStatusDelegate>("principia__FlightPlanGetAnomalousStatus");

			public FlightPlanGetCoastAnalysisDelegate FlightPlanGetCoastAnalysis = Loader.LoadFunction<FlightPlanGetCoastAnalysisDelegate>("principia__FlightPlanGetCoastAnalysis");

			public FlightPlanGetDesiredFinalTimeDelegate FlightPlanGetDesiredFinalTime = Loader.LoadFunction<FlightPlanGetDesiredFinalTimeDelegate>("principia__FlightPlanGetDesiredFinalTime");

			public FlightPlanGetGuidanceDelegate FlightPlanGetGuidance = Loader.LoadFunction<FlightPlanGetGuidanceDelegate>("principia__FlightPlanGetGuidance");

			public FlightPlanGetInitialTimeDelegate FlightPlanGetInitialTime = Loader.LoadFunction<FlightPlanGetInitialTimeDelegate>("principia__FlightPlanGetInitialTime");

			public FlightPlanGetManoeuvreDelegate FlightPlanGetManoeuvre = Loader.LoadFunction<FlightPlanGetManoeuvreDelegate>("principia__FlightPlanGetManoeuvre");

			public FlightPlanGetManoeuvreFrenetTrihedronDelegate FlightPlanGetManoeuvreFrenetTrihedron = Loader.LoadFunction<FlightPlanGetManoeuvreFrenetTrihedronDelegate>("principia__FlightPlanGetManoeuvreFrenetTrihedron");

			public FlightPlanGetManoeuvreInitialPlottedVelocityDelegate FlightPlanGetManoeuvreInitialPlottedVelocity = Loader.LoadFunction<FlightPlanGetManoeuvreInitialPlottedVelocityDelegate>("principia__FlightPlanGetManoeuvreInitialPlottedVelocity");

			public FlightPlanInsertDelegate FlightPlanInsert = Loader.LoadFunction<FlightPlanInsertDelegate>("principia__FlightPlanInsert");

			public FlightPlanNumberOfAnomalousManoeuvresDelegate FlightPlanNumberOfAnomalousManoeuvres = Loader.LoadFunction<FlightPlanNumberOfAnomalousManoeuvresDelegate>("principia__FlightPlanNumberOfAnomalousManoeuvres");

			public FlightPlanNumberOfManoeuvresDelegate FlightPlanNumberOfManoeuvres = Loader.LoadFunction<FlightPlanNumberOfManoeuvresDelegate>("principia__FlightPlanNumberOfManoeuvres");

			public FlightPlanNumberOfSegmentsDelegate FlightPlanNumberOfSegments = Loader.LoadFunction<FlightPlanNumberOfSegmentsDelegate>("principia__FlightPlanNumberOfSegments");

			public FlightPlanOptimizationDriverInProgressDelegate FlightPlanOptimizationDriverInProgress = Loader.LoadFunction<FlightPlanOptimizationDriverInProgressDelegate>("principia__FlightPlanOptimizationDriverInProgress");

			public FlightPlanOptimizationDriverMakeDelegate FlightPlanOptimizationDriverMake = Loader.LoadFunction<FlightPlanOptimizationDriverMakeDelegate>("principia__FlightPlanOptimizationDriverMake");

			public FlightPlanOptimizationDriverStartDelegate FlightPlanOptimizationDriverStart = Loader.LoadFunction<FlightPlanOptimizationDriverStartDelegate>("principia__FlightPlanOptimizationDriverStart");

			public FlightPlanRebaseDelegate FlightPlanRebase = Loader.LoadFunction<FlightPlanRebaseDelegate>("principia__FlightPlanRebase");

			public FlightPlanRemoveDelegate FlightPlanRemove = Loader.LoadFunction<FlightPlanRemoveDelegate>("principia__FlightPlanRemove");

			public FlightPlanRenderedApsidesDelegate FlightPlanRenderedApsides = Loader.LoadFunction<FlightPlanRenderedApsidesDelegate>("principia__FlightPlanRenderedApsides");

			public FlightPlanRenderedClosestApproachesDelegate FlightPlanRenderedClosestApproaches = Loader.LoadFunction<FlightPlanRenderedClosestApproachesDelegate>("principia__FlightPlanRenderedClosestApproaches");

			public FlightPlanRenderedManoeuvreInitialDegreesOfFreedomDelegate FlightPlanRenderedManoeuvreInitialDegreesOfFreedom = Loader.LoadFunction<FlightPlanRenderedManoeuvreInitialDegreesOfFreedomDelegate>("principia__FlightPlanRenderedManoeuvreInitialDegreesOfFreedom");

			public FlightPlanRenderedNodesDelegate FlightPlanRenderedNodes = Loader.LoadFunction<FlightPlanRenderedNodesDelegate>("principia__FlightPlanRenderedNodes");

			public FlightPlanReplaceDelegate FlightPlanReplace = Loader.LoadFunction<FlightPlanReplaceDelegate>("principia__FlightPlanReplace");

			public FlightPlanSelectDelegate FlightPlanSelect = Loader.LoadFunction<FlightPlanSelectDelegate>("principia__FlightPlanSelect");

			public FlightPlanSelectedDelegate FlightPlanSelected = Loader.LoadFunction<FlightPlanSelectedDelegate>("principia__FlightPlanSelected");

			public FlightPlanSetAdaptiveStepParametersDelegate FlightPlanSetAdaptiveStepParameters = Loader.LoadFunction<FlightPlanSetAdaptiveStepParametersDelegate>("principia__FlightPlanSetAdaptiveStepParameters");

			public FlightPlanSetDesiredFinalTimeDelegate FlightPlanSetDesiredFinalTime = Loader.LoadFunction<FlightPlanSetDesiredFinalTimeDelegate>("principia__FlightPlanSetDesiredFinalTime");

			public FlightPlanUpdateFromOptimizationDelegate FlightPlanUpdateFromOptimization = Loader.LoadFunction<FlightPlanUpdateFromOptimizationDelegate>("principia__FlightPlanUpdateFromOptimization");

			public FreeVesselsAndPartsAndCollectPileUpsDelegate FreeVesselsAndPartsAndCollectPileUps = Loader.LoadFunction<FreeVesselsAndPartsAndCollectPileUpsDelegate>("principia__FreeVesselsAndPartsAndCollectPileUps");

			public FutureCatchUpVesselDelegate FutureCatchUpVessel = Loader.LoadFunction<FutureCatchUpVesselDelegate>("principia__FutureCatchUpVessel");

			public FutureTryHarvestVesselCatchUpDelegate FutureTryHarvestVesselCatchUp = Loader.LoadOptionalFunction<FutureTryHarvestVesselCatchUpDelegate>("principia__FutureTryHarvestVesselCatchUp");

			public FutureWaitForVesselToCatchUpDelegate FutureWaitForVesselToCatchUp = Loader.LoadFunction<FutureWaitForVesselToCatchUpDelegate>("principia__FutureWaitForVesselToCatchUp");

			public GetBufferedLoggingDelegate GetBufferedLogging = Loader.LoadFunction<GetBufferedLoggingDelegate>("principia__GetBufferedLogging");

			public GetCPUIDFeatureFlagsDelegate GetCPUIDFeatureFlags = Loader.LoadFunction<GetCPUIDFeatureFlagsDelegate>("principia__GetCPUIDFeatureFlags");

			public GetStderrLoggingDelegate GetStderrLogging = Loader.LoadFunction<GetStderrLoggingDelegate>("principia__GetStderrLogging");

			public GetSuppressedLoggingDelegate GetSuppressedLogging = Loader.LoadFunction<GetSuppressedLoggingDelegate>("principia__GetSuppressedLogging");

			public GetVerboseLoggingDelegate GetVerboseLogging = Loader.LoadFunction<GetVerboseLoggingDelegate>("principia__GetVerboseLogging");

			public GetVersionDelegate GetVersion = Loader.LoadFunction<GetVersionDelegate>("principia__GetVersion");

			public GraphLidovFrozenLineDelegate GraphLidovFrozenLine = Loader.LoadFunction<GraphLidovFrozenLineDelegate>("principia__GraphLidovFrozenLine");

			public GraphLidovMaximalEccentricityLineDelegate GraphLidovMaximalEccentricityLine = Loader.LoadFunction<GraphLidovMaximalEccentricityLineDelegate>("principia__GraphLidovMaximalEccentricityLine");

			public GraphLidovMaximalEccentricityLineC2RangeDelegate GraphLidovMaximalEccentricityLineC2Range = Loader.LoadFunction<GraphLidovMaximalEccentricityLineC2RangeDelegate>("principia__GraphLidovMaximalEccentricityLineC2Range");

			public GraphLidovMaximalInclinationLineDelegate GraphLidovMaximalInclinationLine = Loader.LoadFunction<GraphLidovMaximalInclinationLineDelegate>("principia__GraphLidovMaximalInclinationLine");

			public GraphLidovMaximalInclinationLineC2RangeDelegate GraphLidovMaximalInclinationLineC2Range = Loader.LoadFunction<GraphLidovMaximalInclinationLineC2RangeDelegate>("principia__GraphLidovMaximalInclinationLineC2Range");

			public GraphLidovMinimalEccentricityLeftLineDelegate GraphLidovMinimalEccentricityLeftLine = Loader.LoadFunction<GraphLidovMinimalEccentricityLeftLineDelegate>("principia__GraphLidovMinimalEccentricityLeftLine");

			public GraphLidovMinimalEccentricityLeftLineC2RangeDelegate GraphLidovMinimalEccentricityLeftLineC2Range = Loader.LoadFunction<GraphLidovMinimalEccentricityLeftLineC2RangeDelegate>("principia__GraphLidovMinimalEccentricityLeftLineC2Range");

			public GraphLidovMinimalEccentricityRightLineC2AndC1MaxDelegate GraphLidovMinimalEccentricityRightLineC2AndC1Max = Loader.LoadFunction<GraphLidovMinimalEccentricityRightLineC2AndC1MaxDelegate>("principia__GraphLidovMinimalEccentricityRightLineC2AndC1Max");

			public GraphLidovMinimalInclinationLineDelegate GraphLidovMinimalInclinationLine = Loader.LoadFunction<GraphLidovMinimalInclinationLineDelegate>("principia__GraphLidovMinimalInclinationLine");

			public GraphLidovMinimalInclinationLineC2RangeDelegate GraphLidovMinimalInclinationLineC2Range = Loader.LoadFunction<GraphLidovMinimalInclinationLineC2RangeDelegate>("principia__GraphLidovMinimalInclinationLineC2Range");

			public HasEncounteredApocalypseDelegate HasEncounteredApocalypse = Loader.LoadFunction<HasEncounteredApocalypseDelegate>("principia__HasEncounteredApocalypse");

			public HasVesselDelegate HasVessel = Loader.LoadFunction<HasVesselDelegate>("principia__HasVessel");

			public InitGoogleLoggingDelegate InitGoogleLogging = Loader.LoadFunction<InitGoogleLoggingDelegate>("principia__InitGoogleLogging");

			public InitializeDownsamplingParametersDelegate InitializeDownsamplingParameters = Loader.LoadFunction<InitializeDownsamplingParametersDelegate>("principia__InitializeDownsamplingParameters");

			public InitializeEphemerisParametersDelegate InitializeEphemerisParameters = Loader.LoadFunction<InitializeEphemerisParametersDelegate>("principia__InitializeEphemerisParameters");

			public InitializeHistoryParametersDelegate InitializeHistoryParameters = Loader.LoadFunction<InitializeHistoryParametersDelegate>("principia__InitializeHistoryParameters");

			public InitializePsychohistoryParametersDelegate InitializePsychohistoryParameters = Loader.LoadFunction<InitializePsychohistoryParametersDelegate>("principia__InitializePsychohistoryParameters");

			public InsertCelestialAbsoluteCartesianDelegate InsertCelestialAbsoluteCartesian = Loader.LoadFunction<InsertCelestialAbsoluteCartesianDelegate>("principia__InsertCelestialAbsoluteCartesian");

			public InsertCelestialJacobiKeplerianDelegate InsertCelestialJacobiKeplerian = Loader.LoadFunction<InsertCelestialJacobiKeplerianDelegate>("principia__InsertCelestialJacobiKeplerian");

			public InsertOrKeepVesselDelegate InsertOrKeepVessel = Loader.LoadFunction<InsertOrKeepVesselDelegate>("principia__InsertOrKeepVessel");

			public InsertOrKeepLoadedPartDelegate InsertOrKeepLoadedPart = Loader.LoadFunction<InsertOrKeepLoadedPartDelegate>("principia__InsertOrKeepLoadedPart");

			public InsertUnloadedPartDelegate InsertUnloadedPart = Loader.LoadFunction<InsertUnloadedPartDelegate>("principia__InsertUnloadedPart");

			public IteratorAtEndDelegate IteratorAtEnd = Loader.LoadFunction<IteratorAtEndDelegate>("principia__IteratorAtEnd");

			public IteratorDeleteDelegate IteratorDelete = Loader.LoadFunction<IteratorDeleteDelegate>("principia__IteratorDelete");

			public IteratorGetCelestialNameDelegate IteratorGetCelestialName = Loader.LoadFunction<IteratorGetCelestialNameDelegate>("principia__IteratorGetCelestialName");

			public IteratorGetDiscreteTrajectoryQPDelegate IteratorGetDiscreteTrajectoryQP = Loader.LoadFunction<IteratorGetDiscreteTrajectoryQPDelegate>("principia__IteratorGetDiscreteTrajectoryQP");

			public IteratorGetDiscreteTrajectoryTimeDelegate IteratorGetDiscreteTrajectoryTime = Loader.LoadFunction<IteratorGetDiscreteTrajectoryTimeDelegate>("principia__IteratorGetDiscreteTrajectoryTime");

			public IteratorGetDiscreteTrajectoryXYZDelegate IteratorGetDiscreteTrajectoryXYZ = Loader.LoadFunction<IteratorGetDiscreteTrajectoryXYZDelegate>("principia__IteratorGetDiscreteTrajectoryXYZ");

			public IteratorGetDistinguishedPointsQPDelegate IteratorGetDistinguishedPointsQP = Loader.LoadFunction<IteratorGetDistinguishedPointsQPDelegate>("principia__IteratorGetDistinguishedPointsQP");

			public IteratorGetDistinguishedPointsTimeDelegate IteratorGetDistinguishedPointsTime = Loader.LoadFunction<IteratorGetDistinguishedPointsTimeDelegate>("principia__IteratorGetDistinguishedPointsTime");

			public IteratorGetDistinguishedPointsXYZDelegate IteratorGetDistinguishedPointsXYZ = Loader.LoadFunction<IteratorGetDistinguishedPointsXYZDelegate>("principia__IteratorGetDistinguishedPointsXYZ");

			public IteratorGetNodeDelegate IteratorGetNode = Loader.LoadFunction<IteratorGetNodeDelegate>("principia__IteratorGetNode");

			public IteratorGetRP2LinesIteratorDelegate IteratorGetRP2LinesIterator = Loader.LoadFunction<IteratorGetRP2LinesIteratorDelegate>("principia__IteratorGetRP2LinesIterator");

			public IteratorGetRP2LineXYDelegate IteratorGetRP2LineXY = Loader.LoadFunction<IteratorGetRP2LineXYDelegate>("principia__IteratorGetRP2LineXY");

			public IteratorGetVesselGuidDelegate IteratorGetVesselGuid = Loader.LoadFunction<IteratorGetVesselGuidDelegate>("principia__IteratorGetVesselGuid");

			public IteratorGetPlottableElementsDelegate IteratorGetPlottableElements = Loader.LoadFunction<IteratorGetPlottableElementsDelegate>("principia__IteratorGetPlottableElements");

			public IteratorIncrementDelegate IteratorIncrement = Loader.LoadFunction<IteratorIncrementDelegate>("principia__IteratorIncrement");

			public IteratorResetDelegate IteratorReset = Loader.LoadFunction<IteratorResetDelegate>("principia__IteratorReset");

			public IteratorSizeDelegate IteratorSize = Loader.LoadFunction<IteratorSizeDelegate>("principia__IteratorSize");

			public LogErrorDelegate LogError = Loader.LoadFunction<LogErrorDelegate>("principia__LogError");

			public LogFatalDelegate LogFatal = Loader.LoadFunction<LogFatalDelegate>("principia__LogFatal");

			public LogInfoDelegate LogInfo = Loader.LoadFunction<LogInfoDelegate>("principia__LogInfo");

			public LogWarningDelegate LogWarning = Loader.LoadFunction<LogWarningDelegate>("principia__LogWarning");

			public MonitorSetNameDelegate MonitorSetName = Loader.LoadFunction<MonitorSetNameDelegate>("principia__MonitorSetName");

			public MonitorStartDelegate MonitorStart = Loader.LoadFunction<MonitorStartDelegate>("principia__MonitorStart");

			public MonitorStopDelegate MonitorStop = Loader.LoadFunction<MonitorStopDelegate>("principia__MonitorStop");

			public NavballOrientationDelegate NavballOrientation = Loader.LoadFunction<NavballOrientationDelegate>("principia__NavballOrientation");

			public NewPluginDelegate NewPlugin = Loader.LoadFunction<NewPluginDelegate>("principia__NewPlugin");

			public PartAngularMomentumFromAngularVelocityDelegate PartAngularMomentumFromAngularVelocity = Loader.LoadFunction<PartAngularMomentumFromAngularVelocityDelegate>("principia__PartAngularMomentumFromAngularVelocity");

			public PartApplyIntrinsicForceDelegate PartApplyIntrinsicForce = Loader.LoadFunction<PartApplyIntrinsicForceDelegate>("principia__PartApplyIntrinsicForce");

			public PartApplyIntrinsicForceAtPositionDelegate PartApplyIntrinsicForceAtPosition = Loader.LoadFunction<PartApplyIntrinsicForceAtPositionDelegate>("principia__PartApplyIntrinsicForceAtPosition");

			public PartApplyIntrinsicTorqueDelegate PartApplyIntrinsicTorque = Loader.LoadFunction<PartApplyIntrinsicTorqueDelegate>("principia__PartApplyIntrinsicTorque");

			public PartDragTorqueFromAngularVelocityDelegate PartDragTorqueFromAngularVelocity = Loader.LoadFunction<PartDragTorqueFromAngularVelocityDelegate>("principia__PartDragTorqueFromAngularVelocity");

			public PartGetActualRigidMotionDelegate PartGetActualRigidMotion = Loader.LoadFunction<PartGetActualRigidMotionDelegate>("principia__PartGetActualRigidMotion");

			public PartIsTruthfulDelegate PartIsTruthful = Loader.LoadFunction<PartIsTruthfulDelegate>("principia__PartIsTruthful");

			public PartSetApparentRigidMotionDelegate PartSetApparentRigidMotion = Loader.LoadFunction<PartSetApparentRigidMotionDelegate>("principia__PartSetApparentRigidMotion");

			public PlanetariumCreateDelegate PlanetariumCreate = Loader.LoadFunction<PlanetariumCreateDelegate>("principia__PlanetariumCreate");

			public PlanetariumDeleteDelegate PlanetariumDelete = Loader.LoadFunction<PlanetariumDeleteDelegate>("principia__PlanetariumDelete");

			public PlanetariumPlotCelestialFutureTrajectoryDelegate PlanetariumPlotCelestialFutureTrajectory = Loader.LoadFunction<PlanetariumPlotCelestialFutureTrajectoryDelegate>("principia__PlanetariumPlotCelestialFutureTrajectory");

			public PlanetariumPlotCelestialPastTrajectoryDelegate PlanetariumPlotCelestialPastTrajectory = Loader.LoadFunction<PlanetariumPlotCelestialPastTrajectoryDelegate>("principia__PlanetariumPlotCelestialPastTrajectory");

			public PlanetariumPlotEquipotentialDelegate PlanetariumPlotEquipotential = Loader.LoadFunction<PlanetariumPlotEquipotentialDelegate>("principia__PlanetariumPlotEquipotential");

			public PlanetariumPlotFlightPlanSegmentDelegate PlanetariumPlotFlightPlanSegment = Loader.LoadFunction<PlanetariumPlotFlightPlanSegmentDelegate>("principia__PlanetariumPlotFlightPlanSegment");

			public PlanetariumPlotFlightPlanSegmentDirectDelegate PlanetariumPlotFlightPlanSegmentDirect = Loader.LoadOptionalFunction<PlanetariumPlotFlightPlanSegmentDirectDelegate>("principia__PlanetariumPlotFlightPlanSegmentDirect");

			public PlanetariumPlotPredictionDelegate PlanetariumPlotPrediction = Loader.LoadFunction<PlanetariumPlotPredictionDelegate>("principia__PlanetariumPlotPrediction");

			public PlanetariumPlotPredictionDirectDelegate PlanetariumPlotPredictionDirect = Loader.LoadOptionalFunction<PlanetariumPlotPredictionDirectDelegate>("principia__PlanetariumPlotPredictionDirect");

			public PlanetariumPlotPsychohistoryDelegate PlanetariumPlotPsychohistory = Loader.LoadFunction<PlanetariumPlotPsychohistoryDelegate>("principia__PlanetariumPlotPsychohistory");

			public PlanetariumPlotPsychohistoryDirectDelegate PlanetariumPlotPsychohistoryDirect = Loader.LoadOptionalFunction<PlanetariumPlotPsychohistoryDirectDelegate>("principia__PlanetariumPlotPsychohistoryDirect");

			public PluginReaderAwaitDelegate PluginReaderAwait = Loader.LoadFunction<PluginReaderAwaitDelegate>("principia__PluginReaderAwait");

			public PluginReaderGetDelegate PluginReaderGet = Loader.LoadFunction<PluginReaderGetDelegate>("principia__PluginReaderGet");

			public PluginReaderLogsDelegate PluginReaderLogs = Loader.LoadFunction<PluginReaderLogsDelegate>("principia__PluginReaderLogs");

			public PluginReaderWillBeSlowDelegate PluginReaderWillBeSlow = Loader.LoadFunction<PluginReaderWillBeSlowDelegate>("principia__PluginReaderWillBeSlow");

			public PrepareToReportCollisionsDelegate PrepareToReportCollisions = Loader.LoadFunction<PrepareToReportCollisionsDelegate>("principia__PrepareToReportCollisions");

			public RenderedPredictionApsidesDelegate RenderedPredictionApsides = Loader.LoadFunction<RenderedPredictionApsidesDelegate>("principia__RenderedPredictionApsides");

			public RenderedPredictionClosestApproachesDelegate RenderedPredictionClosestApproaches = Loader.LoadFunction<RenderedPredictionClosestApproachesDelegate>("principia__RenderedPredictionClosestApproaches");

			public RenderedPredictionNodesDelegate RenderedPredictionNodes = Loader.LoadFunction<RenderedPredictionNodesDelegate>("principia__RenderedPredictionNodes");

			public ReportGroundCollisionDelegate ReportGroundCollision = Loader.LoadFunction<ReportGroundCollisionDelegate>("principia__ReportGroundCollision");

			public ReportPartCollisionDelegate ReportPartCollision = Loader.LoadFunction<ReportPartCollisionDelegate>("principia__ReportPartCollision");

			public SayHelloDelegate SayHello = Loader.LoadFunction<SayHelloDelegate>("principia__SayHello");

			public SayNotFoundDelegate SayNotFound = Loader.LoadFunction<SayNotFoundDelegate>("principia__SayNotFound");

			public SerializePluginDelegate SerializePlugin = Loader.LoadFunction<SerializePluginDelegate>("principia__SerializePlugin");

			public SetBufferedLoggingDelegate SetBufferedLogging = Loader.LoadFunction<SetBufferedLoggingDelegate>("principia__SetBufferedLogging");

			public SetFlagDelegate SetFlag = Loader.LoadFunction<SetFlagDelegate>("principia__SetFlag");

			public SetMainBodyDelegate SetMainBody = Loader.LoadFunction<SetMainBodyDelegate>("principia__SetMainBody");

			public SetPlottingFrameDelegate SetPlottingFrame = Loader.LoadFunction<SetPlottingFrameDelegate>("principia__SetPlottingFrame");

			public SetStderrLoggingDelegate SetStderrLogging = Loader.LoadFunction<SetStderrLoggingDelegate>("principia__SetStderrLogging");

			public SetSuppressedLoggingDelegate SetSuppressedLogging = Loader.LoadFunction<SetSuppressedLoggingDelegate>("principia__SetSuppressedLogging");

			public SetTargetVesselDelegate SetTargetVessel = Loader.LoadFunction<SetTargetVesselDelegate>("principia__SetTargetVessel");

			public SetVerboseLoggingDelegate SetVerboseLogging = Loader.LoadFunction<SetVerboseLoggingDelegate>("principia__SetVerboseLogging");

			public SetWorldRotationalReferenceFrameDelegate SetWorldRotationalReferenceFrame = Loader.LoadFunction<SetWorldRotationalReferenceFrameDelegate>("principia__SetWorldRotationalReferenceFrame");

			public UnmanageableVesselVelocityDelegate UnmanageableVesselVelocity = Loader.LoadFunction<UnmanageableVesselVelocityDelegate>("principia__UnmanageableVesselVelocity");

			public UpdateCelestialHierarchyDelegate UpdateCelestialHierarchy = Loader.LoadFunction<UpdateCelestialHierarchyDelegate>("principia__UpdateCelestialHierarchy");

			public UpdatePredictionDelegate UpdatePrediction = Loader.LoadFunction<UpdatePredictionDelegate>("principia__UpdatePrediction");

			public VesselBinormalDelegate VesselBinormal = Loader.LoadFunction<VesselBinormalDelegate>("principia__VesselBinormal");

			public VesselClearPlottingFramePayloadDelegate VesselClearPlottingFramePayload = Loader.LoadFunction<VesselClearPlottingFramePayloadDelegate>("principia__VesselClearPlottingFramePayload");

			public VesselFromParentDelegate VesselFromParent = Loader.LoadFunction<VesselFromParentDelegate>("principia__VesselFromParent");

			public VesselGetAnalysisDelegate VesselGetAnalysis = Loader.LoadFunction<VesselGetAnalysisDelegate>("principia__VesselGetAnalysis");

			public VesselGetPlottingFramePayloadDelegate VesselGetPlottingFramePayload = Loader.LoadFunction<VesselGetPlottingFramePayloadDelegate>("principia__VesselGetPlottingFramePayload");

			public VesselGetPredictionAdaptiveStepParametersDelegate VesselGetPredictionAdaptiveStepParameters = Loader.LoadFunction<VesselGetPredictionAdaptiveStepParametersDelegate>("principia__VesselGetPredictionAdaptiveStepParameters");

			public VesselNormalDelegate VesselNormal = Loader.LoadFunction<VesselNormalDelegate>("principia__VesselNormal");

			public VesselRequestAnalysisDelegate VesselRequestAnalysis = Loader.LoadFunction<VesselRequestAnalysisDelegate>("principia__VesselRequestAnalysis");

			public VesselSetPlottingFramePayloadDelegate VesselSetPlottingFramePayload = Loader.LoadFunction<VesselSetPlottingFramePayloadDelegate>("principia__VesselSetPlottingFramePayload");

			public VesselSetPredictionAdaptiveStepParametersDelegate VesselSetPredictionAdaptiveStepParameters = Loader.LoadFunction<VesselSetPredictionAdaptiveStepParametersDelegate>("principia__VesselSetPredictionAdaptiveStepParameters");

			public VesselTangentDelegate VesselTangent = Loader.LoadFunction<VesselTangentDelegate>("principia__VesselTangent");

			public VesselVelocityDelegate VesselVelocity = Loader.LoadFunction<VesselVelocityDelegate>("principia__VesselVelocity");

			public StateBusCreateChannelDelegate StateBusCreateChannel = Loader.LoadOptionalFunction<StateBusCreateChannelDelegate>("principia__StateBusCreateChannel");

			public StateBusDestroyChannelDelegate StateBusDestroyChannel = Loader.LoadOptionalFunction<StateBusDestroyChannelDelegate>("principia__StateBusDestroyChannel");

			public StateBusCommitWriteDelegate StateBusCommitWrite = Loader.LoadOptionalFunction<StateBusCommitWriteDelegate>("principia__StateBusCommitWrite");

			public StateBusAcquireReadDelegate StateBusAcquireRead = Loader.LoadOptionalFunction<StateBusAcquireReadDelegate>("principia__StateBusAcquireRead");

			public CommandBusCreateDelegate CommandBusCreate = Loader.LoadOptionalFunction<CommandBusCreateDelegate>("principia__CommandBusCreate");

			public CommandBusDestroyDelegate CommandBusDestroy = Loader.LoadOptionalFunction<CommandBusDestroyDelegate>("principia__CommandBusDestroy");

			public CommandBusTryPushDelegate CommandBusTryPush = Loader.LoadOptionalFunction<CommandBusTryPushDelegate>("principia__CommandBusTryPush");

			public CommandBusTryPopDelegate CommandBusTryPop = Loader.LoadOptionalFunction<CommandBusTryPopDelegate>("principia__CommandBusTryPop");

			public BatchInsertOrKeepLoadedPartsDelegate BatchInsertOrKeepLoadedParts = Loader.LoadOptionalFunction<BatchInsertOrKeepLoadedPartsDelegate>("principia__BatchInsertOrKeepLoadedParts");

			public BatchPartSetApparentRigidMotionDelegate BatchPartSetApparentRigidMotion = Loader.LoadOptionalFunction<BatchPartSetApparentRigidMotionDelegate>("principia__BatchPartSetApparentRigidMotion");

			public BatchPartGetActualRigidMotionDelegate BatchPartGetActualRigidMotion = Loader.LoadOptionalFunction<BatchPartGetActualRigidMotionDelegate>("principia__BatchPartGetActualRigidMotion");
		}



		internal static void ActivatePlayer()
		{
			symbols_.ActivatePlayer();
		}

		internal static void ActivateRecorder(bool activate)
		{
			symbols_.ActivateRecorder(activate);
		}

		internal static void AdvanceTime(this IntPtr plugin, double t, double planetarium_rotation)
		{
			symbols_.AdvanceTime(plugin, t, planetarium_rotation);
		}

		internal static WXYZ CameraReferenceRotation(this IntPtr plugin)
		{
			return symbols_.CameraReferenceRotation(plugin);
		}

		internal static double CameraScale(this IntPtr plugin)
		{
			return symbols_.CameraScale(plugin);
		}

		internal static void CatchUpLaggingVessels(this IntPtr plugin, out DisposableIterator collided_vessels)
		{
			symbols_.CatchUpLaggingVessels(plugin, out collided_vessels);
		}

		internal static QP CelestialFromParent(this IntPtr plugin, int celestial_index)
		{
			return symbols_.CelestialFromParent(plugin, celestial_index);
		}

		internal static DisposableIterator CelestialGetAllNames(this IntPtr plugin)
		{
			return symbols_.CelestialGetAllNames(plugin);
		}

		internal static double CelestialInitialRotationInDegrees(this IntPtr plugin, int celestial_index)
		{
			return symbols_.CelestialInitialRotationInDegrees(plugin, celestial_index);
		}

		internal static WXYZ CelestialRotation(this IntPtr plugin, int index)
		{
			return symbols_.CelestialRotation(plugin, index);
		}

		internal static double CelestialRotationPeriod(this IntPtr plugin, int celestial_index)
		{
			return symbols_.CelestialRotationPeriod(plugin, celestial_index);
		}

		internal static WXYZ CelestialSphereRotation(this IntPtr plugin)
		{
			return symbols_.CelestialSphereRotation(plugin);
		}

		internal static QP CelestialWorldDegreesOfFreedom(this IntPtr plugin, int index, Origin origin, double time)
		{
			return symbols_.CelestialWorldDegreesOfFreedom(plugin, index, origin, time);
		}

		internal static bool CollisionDeleteExecutor(this IntPtr plugin, ref IntPtr executor, out TQP collision)
		{
			return symbols_.CollisionDeleteExecutor(plugin, ref executor, out collision);
		}

		internal static bool CollisionGetLatitudeLongitude(this IntPtr executor, out double latitude_in_degrees, out double longitude_in_degrees)
		{
			return symbols_.CollisionGetLatitudeLongitude(executor, out latitude_in_degrees, out longitude_in_degrees);
		}

		internal static IntPtr CollisionNewFlightPlanExecutor(this IntPtr plugin, int celestial_index, XYZ sun_world_position, int max_points, string vessel_guid)
		{
			return symbols_.CollisionNewFlightPlanExecutor(plugin, celestial_index, sun_world_position, max_points, vessel_guid);
		}

		internal static IntPtr CollisionNewPredictionExecutor(this IntPtr plugin, int celestial_index, XYZ sun_world_position, int max_points, string vessel_guid)
		{
			return symbols_.CollisionNewPredictionExecutor(plugin, celestial_index, sun_world_position, max_points, vessel_guid);
		}

		internal static void CollisionSetRadius(this IntPtr executor, double radius)
		{
			symbols_.CollisionSetRadius(executor, radius);
		}

		internal static void ClearFlags()
		{
			symbols_.ClearFlags();
		}

		internal static void ClearTargetVessel(this IntPtr plugin)
		{
			symbols_.ClearTargetVessel(plugin);
		}

		internal static void ClearWorldRotationalReferenceFrame(this IntPtr plugin)
		{
			symbols_.ClearWorldRotationalReferenceFrame(plugin);
		}

		internal static double CurrentTime(this IntPtr plugin)
		{
			return symbols_.CurrentTime(plugin);
		}

		internal static void DeleteInterchange(ref IntPtr native_pointer)
		{
			symbols_.DeleteInterchange(ref native_pointer);
		}

		internal static void DeletePlugin(ref IntPtr plugin)
		{
			symbols_.DeletePlugin(ref plugin);
		}

		internal static void DeleteString(ref IntPtr native_string)
		{
			symbols_.DeleteString(ref native_string);
		}

		internal static void DeleteU16String(ref IntPtr native_string)
		{
			symbols_.DeleteU16String(ref native_string);
		}

		internal static void DeserializePlugin(string serialization, ref IntPtr deserializer, ref IntPtr plugin_reader, string compressor, string encoder)
		{
			symbols_.DeserializePlugin(serialization, ref deserializer, ref plugin_reader, compressor, encoder);
		}

		internal static void EndInitialization(this IntPtr plugin)
		{
			symbols_.EndInitialization(plugin);
		}

		internal static int EquipotentialCount(this IntPtr plugin)
		{
			return symbols_.EquipotentialCount(plugin);
		}

		internal static int FlightPlanCount(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.FlightPlanCount(plugin, vessel_guid);
		}

		internal static void FlightPlanCreate(this IntPtr plugin, string vessel_guid, double final_time, double mass_in_tonnes)
		{
			symbols_.FlightPlanCreate(plugin, vessel_guid, final_time, mass_in_tonnes);
		}

		internal static void FlightPlanDelete(this IntPtr plugin, string vessel_guid)
		{
			symbols_.FlightPlanDelete(plugin, vessel_guid);
		}

		internal static void FlightPlanDuplicate(this IntPtr plugin, string vessel_guid)
		{
			symbols_.FlightPlanDuplicate(plugin, vessel_guid);
		}

		internal static bool FlightPlanExists(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.FlightPlanExists(plugin, vessel_guid);
		}

		internal static FlightPlanAdaptiveStepParameters FlightPlanGetAdaptiveStepParameters(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.FlightPlanGetAdaptiveStepParameters(plugin, vessel_guid);
		}

		internal static double FlightPlanGetActualFinalTime(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.FlightPlanGetActualFinalTime(plugin, vessel_guid);
		}

		internal static Status FlightPlanGetAnomalousStatus(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.FlightPlanGetAnomalousStatus(plugin, vessel_guid);
		}

		internal static OrbitAnalysis FlightPlanGetCoastAnalysis(this IntPtr plugin, string vessel_guid, BoxedInt32 revolutions_per_cycle, BoxedInt32 days_per_cycle, int ground_track_revolution, int index)
		{
			return symbols_.FlightPlanGetCoastAnalysis(plugin, vessel_guid, revolutions_per_cycle, days_per_cycle, ground_track_revolution, index);
		}

		internal static double FlightPlanGetDesiredFinalTime(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.FlightPlanGetDesiredFinalTime(plugin, vessel_guid);
		}

		internal static XYZ FlightPlanGetGuidance(this IntPtr plugin, string vessel_guid, int index)
		{
			return symbols_.FlightPlanGetGuidance(plugin, vessel_guid, index);
		}

		internal static double FlightPlanGetInitialTime(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.FlightPlanGetInitialTime(plugin, vessel_guid);
		}

		internal static NavigationManoeuvre FlightPlanGetManoeuvre(this IntPtr plugin, string vessel_guid, int index)
		{
			return symbols_.FlightPlanGetManoeuvre(plugin, vessel_guid, index);
		}

		internal static NavigationManoeuvreFrenetTrihedron FlightPlanGetManoeuvreFrenetTrihedron(this IntPtr plugin, string vessel_guid, int index)
		{
			return symbols_.FlightPlanGetManoeuvreFrenetTrihedron(plugin, vessel_guid, index);
		}

		internal static XYZ FlightPlanGetManoeuvreInitialPlottedVelocity(this IntPtr plugin, string vessel_guid, int index)
		{
			return symbols_.FlightPlanGetManoeuvreInitialPlottedVelocity(plugin, vessel_guid, index);
		}

		internal static Status FlightPlanInsert(this IntPtr plugin, string vessel_guid, Burn burn, int index)
		{
			return symbols_.FlightPlanInsert(plugin, vessel_guid, burn, index);
		}

		internal static int FlightPlanNumberOfAnomalousManoeuvres(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.FlightPlanNumberOfAnomalousManoeuvres(plugin, vessel_guid);
		}

		internal static int FlightPlanNumberOfManoeuvres(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.FlightPlanNumberOfManoeuvres(plugin, vessel_guid);
		}

		internal static int FlightPlanNumberOfSegments(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.FlightPlanNumberOfSegments(plugin, vessel_guid);
		}

		internal static int FlightPlanOptimizationDriverInProgress(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.FlightPlanOptimizationDriverInProgress(plugin, vessel_guid);
		}

		internal static void FlightPlanOptimizationDriverMake(this IntPtr plugin, string vessel_guid, double distance, BoxedDouble inclination_in_degrees, int celestial_index, NavigationFrameParameters navigation_frame_parameters)
		{
			symbols_.FlightPlanOptimizationDriverMake(plugin, vessel_guid, distance, inclination_in_degrees, celestial_index, navigation_frame_parameters);
		}

		internal static void FlightPlanOptimizationDriverStart(this IntPtr plugin, string vessel_guid, int manoeuvre_index)
		{
			symbols_.FlightPlanOptimizationDriverStart(plugin, vessel_guid, manoeuvre_index);
		}

		internal static Status FlightPlanRebase(this IntPtr plugin, string vessel_guid, double mass_in_tonnes)
		{
			return symbols_.FlightPlanRebase(plugin, vessel_guid, mass_in_tonnes);
		}

		internal static Status FlightPlanRemove(this IntPtr plugin, string vessel_guid, int index)
		{
			return symbols_.FlightPlanRemove(plugin, vessel_guid, index);
		}

		internal static void FlightPlanRenderedApsides(this IntPtr plugin, string vessel_guid, BoxedDouble t_max, int celestial_index, XYZ sun_world_position, int max_points, out DisposableIterator apoapsides, out DisposableIterator periapsides)
		{
			symbols_.FlightPlanRenderedApsides(plugin, vessel_guid, t_max, celestial_index, sun_world_position, max_points, out apoapsides, out periapsides);
		}

		internal static void FlightPlanRenderedClosestApproaches(this IntPtr plugin, string vessel_guid, XYZ sun_world_position, int max_points, out DisposableIterator closest_approaches)
		{
			symbols_.FlightPlanRenderedClosestApproaches(plugin, vessel_guid, sun_world_position, max_points, out closest_approaches);
		}

		internal static DisposableIterator FlightPlanRenderedManoeuvreInitialDegreesOfFreedom(this IntPtr plugin, string vessel_guid, XYZ sun_world_position, int index)
		{
			return symbols_.FlightPlanRenderedManoeuvreInitialDegreesOfFreedom(plugin, vessel_guid, sun_world_position, index);
		}

		internal static void FlightPlanRenderedNodes(this IntPtr plugin, string vessel_guid, BoxedDouble t_max, XYZ sun_world_position, int max_points, out DisposableIterator ascending, out DisposableIterator descending)
		{
			symbols_.FlightPlanRenderedNodes(plugin, vessel_guid, t_max, sun_world_position, max_points, out ascending, out descending);
		}

		internal static Status FlightPlanReplace(this IntPtr plugin, string vessel_guid, Burn burn, int index)
		{
			return symbols_.FlightPlanReplace(plugin, vessel_guid, burn, index);
		}

		internal static void FlightPlanSelect(this IntPtr plugin, string vessel_guid, int index)
		{
			symbols_.FlightPlanSelect(plugin, vessel_guid, index);
		}

		internal static int FlightPlanSelected(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.FlightPlanSelected(plugin, vessel_guid);
		}

		internal static Status FlightPlanSetAdaptiveStepParameters(this IntPtr plugin, string vessel_guid, FlightPlanAdaptiveStepParameters flight_plan_adaptive_step_parameters)
		{
			return symbols_.FlightPlanSetAdaptiveStepParameters(plugin, vessel_guid, flight_plan_adaptive_step_parameters);
		}

		internal static Status FlightPlanSetDesiredFinalTime(this IntPtr plugin, string vessel_guid, double final_time)
		{
			return symbols_.FlightPlanSetDesiredFinalTime(plugin, vessel_guid, final_time);
		}

		internal static bool FlightPlanUpdateFromOptimization(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.FlightPlanUpdateFromOptimization(plugin, vessel_guid);
		}

		internal static void FreeVesselsAndPartsAndCollectPileUps(this IntPtr plugin, double delta_t)
		{
			symbols_.FreeVesselsAndPartsAndCollectPileUps(plugin, delta_t);
		}

		internal static IntPtr FutureCatchUpVessel(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.FutureCatchUpVessel(plugin, vessel_guid);
		}

		internal static bool FutureTryHarvestVesselCatchUp(this IntPtr plugin, ref IntPtr future, out DisposableIterator collided_vessels)
		{
			if (symbols_.FutureTryHarvestVesselCatchUp != null) {
				return symbols_.FutureTryHarvestVesselCatchUp(plugin, ref future, out collided_vessels);
			}
			symbols_.FutureWaitForVesselToCatchUp(plugin, ref future, out collided_vessels);
			return true;
		}

		internal static void FutureWaitForVesselToCatchUp(this IntPtr plugin, ref IntPtr future, out DisposableIterator collided_vessels)
		{
			symbols_.FutureWaitForVesselToCatchUp(plugin, ref future, out collided_vessels);
		}

		internal static int GetBufferedLogging()
		{
			return symbols_.GetBufferedLogging();
		}

		internal static void GetCPUIDFeatureFlags(out bool has_avx, out bool has_fma)
		{
			symbols_.GetCPUIDFeatureFlags(out has_avx, out has_fma);
		}

		internal static int GetStderrLogging()
		{
			return symbols_.GetStderrLogging();
		}

		internal static int GetSuppressedLogging()
		{
			return symbols_.GetSuppressedLogging();
		}

		internal static int GetVerboseLogging()
		{
			return symbols_.GetVerboseLogging();
		}

		internal static void GetVersion(out string build_date, out string version, out string platform)
		{
			symbols_.GetVersion(out build_date, out version, out platform);
		}

		internal static double GraphLidovFrozenLine(double c2)
		{
			return symbols_.GraphLidovFrozenLine(c2);
		}

		internal static double GraphLidovMaximalEccentricityLine(double eccentricity, double c2)
		{
			return symbols_.GraphLidovMaximalEccentricityLine(eccentricity, c2);
		}

		internal static Interval GraphLidovMaximalEccentricityLineC2Range(double eccentricity)
		{
			return symbols_.GraphLidovMaximalEccentricityLineC2Range(eccentricity);
		}

		internal static double GraphLidovMaximalInclinationLine(double inclination_in_degrees, double c2)
		{
			return symbols_.GraphLidovMaximalInclinationLine(inclination_in_degrees, c2);
		}

		internal static Interval GraphLidovMaximalInclinationLineC2Range(double inclination_in_degrees)
		{
			return symbols_.GraphLidovMaximalInclinationLineC2Range(inclination_in_degrees);
		}

		internal static double GraphLidovMinimalEccentricityLeftLine(double eccentricity, double c2)
		{
			return symbols_.GraphLidovMinimalEccentricityLeftLine(eccentricity, c2);
		}

		internal static Interval GraphLidovMinimalEccentricityLeftLineC2Range(double eccentricity)
		{
			return symbols_.GraphLidovMinimalEccentricityLeftLineC2Range(eccentricity);
		}

		internal static void GraphLidovMinimalEccentricityRightLineC2AndC1Max(double eccentricity, out double c2, out double c1_max)
		{
			symbols_.GraphLidovMinimalEccentricityRightLineC2AndC1Max(eccentricity, out c2, out c1_max);
		}

		internal static double GraphLidovMinimalInclinationLine(double inclination_in_degrees, double c2)
		{
			return symbols_.GraphLidovMinimalInclinationLine(inclination_in_degrees, c2);
		}

		internal static Interval GraphLidovMinimalInclinationLineC2Range(double inclination_in_degrees)
		{
			return symbols_.GraphLidovMinimalInclinationLineC2Range(inclination_in_degrees);
		}

		internal static bool HasEncounteredApocalypse(this IntPtr plugin, out string details)
		{
			return symbols_.HasEncounteredApocalypse(plugin, out details);
		}

		internal static bool HasVessel(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.HasVessel(plugin, vessel_guid);
		}

		internal static void InitGoogleLogging()
		{
			symbols_.InitGoogleLogging();
		}

		internal static void InitializeDownsamplingParameters(this IntPtr plugin, ConfigurationDownsamplingParameters downsampling_parameters)
		{
			symbols_.InitializeDownsamplingParameters(plugin, downsampling_parameters);
		}

		internal static void InitializeEphemerisParameters(this IntPtr plugin, ConfigurationAccuracyParameters accuracy_parameters, ConfigurationFixedStepParameters fixed_step_parameters)
		{
			symbols_.InitializeEphemerisParameters(plugin, accuracy_parameters, fixed_step_parameters);
		}

		internal static void InitializeHistoryParameters(this IntPtr plugin, ConfigurationFixedStepParameters fixed_step_parameters)
		{
			symbols_.InitializeHistoryParameters(plugin, fixed_step_parameters);
		}

		internal static void InitializePsychohistoryParameters(this IntPtr plugin, ConfigurationAdaptiveStepParameters parameters)
		{
			symbols_.InitializePsychohistoryParameters(plugin, parameters);
		}

		internal static void InsertCelestialAbsoluteCartesian(this IntPtr plugin, int celestial_index, BoxedInt32 parent_index, BodyParameters body_parameters, string x, string y, string z, string vx, string vy, string vz)
		{
			symbols_.InsertCelestialAbsoluteCartesian(plugin, celestial_index, parent_index, body_parameters, x, y, z, vx, vy, vz);
		}

		internal static void InsertCelestialJacobiKeplerian(this IntPtr plugin, int celestial_index, BoxedInt32 parent_index, BodyParameters body_parameters, BoxedKeplerianElements keplerian_elements)
		{
			symbols_.InsertCelestialJacobiKeplerian(plugin, celestial_index, parent_index, body_parameters, keplerian_elements);
		}

		internal static void InsertOrKeepVessel(this IntPtr plugin, string vessel_guid, string vessel_name, int parent_index, bool loaded, out bool inserted)
		{
			symbols_.InsertOrKeepVessel(plugin, vessel_guid, vessel_name, parent_index, loaded, out inserted);
		}

		internal static void InsertOrKeepLoadedPart(this IntPtr plugin, uint part_id, string name, double mass_in_tonnes, XYZ centre_of_mass, XYZ moments_of_inertia_in_tonnes, WXYZ principal_axes_rotation, bool is_solid_rocket_motor, string vessel_guid, int main_body_index, QP main_body_world_degrees_of_freedom, QP part_world_degrees_of_freedom, WXYZ part_rotation, XYZ part_angular_velocity, double delta_t)
		{
			symbols_.InsertOrKeepLoadedPart(plugin, part_id, name, mass_in_tonnes, centre_of_mass, moments_of_inertia_in_tonnes, principal_axes_rotation, is_solid_rocket_motor, vessel_guid, main_body_index, main_body_world_degrees_of_freedom, part_world_degrees_of_freedom, part_rotation, part_angular_velocity, delta_t);
		}

		internal static void InsertUnloadedPart(this IntPtr plugin, uint part_id, string name, string vessel_guid, QP from_parent)
		{
			symbols_.InsertUnloadedPart(plugin, part_id, name, vessel_guid, from_parent);
		}

		internal static bool IteratorAtEnd(this DisposableIterator iterator)
		{
			return symbols_.IteratorAtEnd(iterator);
		}

		internal static void IteratorDelete(ref IntPtr iterator)
		{
			symbols_.IteratorDelete(ref iterator);
		}

		internal static string IteratorGetCelestialName(this DisposableIterator iterator)
		{
			return symbols_.IteratorGetCelestialName(iterator);
		}

		internal static QP IteratorGetDiscreteTrajectoryQP(this DisposableIterator iterator)
		{
			return symbols_.IteratorGetDiscreteTrajectoryQP(iterator);
		}

		internal static double IteratorGetDiscreteTrajectoryTime(this DisposableIterator iterator)
		{
			return symbols_.IteratorGetDiscreteTrajectoryTime(iterator);
		}

		internal static XYZ IteratorGetDiscreteTrajectoryXYZ(this DisposableIterator iterator)
		{
			return symbols_.IteratorGetDiscreteTrajectoryXYZ(iterator);
		}

		internal static QP IteratorGetDistinguishedPointsQP(this DisposableIterator iterator)
		{
			return symbols_.IteratorGetDistinguishedPointsQP(iterator);
		}

		internal static double IteratorGetDistinguishedPointsTime(this DisposableIterator iterator)
		{
			return symbols_.IteratorGetDistinguishedPointsTime(iterator);
		}

		internal static XYZ IteratorGetDistinguishedPointsXYZ(this DisposableIterator iterator)
		{
			return symbols_.IteratorGetDistinguishedPointsXYZ(iterator);
		}

		internal static Node IteratorGetNode(this DisposableIterator iterator)
		{
			return symbols_.IteratorGetNode(iterator);
		}

		internal static DisposableIterator IteratorGetRP2LinesIterator(this DisposableIterator iterator)
		{
			return symbols_.IteratorGetRP2LinesIterator(iterator);
		}

		internal static XY IteratorGetRP2LineXY(this DisposableIterator iterator)
		{
			return symbols_.IteratorGetRP2LineXY(iterator);
		}

		internal static string IteratorGetVesselGuid(this DisposableIterator iterator)
		{
			return symbols_.IteratorGetVesselGuid(iterator);
		}

		internal static PlottableElements IteratorGetPlottableElements(this DisposableIterator iterator)
		{
			return symbols_.IteratorGetPlottableElements(iterator);
		}

		internal static void IteratorIncrement(this DisposableIterator iterator)
		{
			symbols_.IteratorIncrement(iterator);
		}

		internal static void IteratorReset(this DisposableIterator iterator)
		{
			symbols_.IteratorReset(iterator);
		}

		internal static int IteratorSize(this DisposableIterator iterator)
		{
			return symbols_.IteratorSize(iterator);
		}

		internal static void LogError(string file, int line, string text)
		{
			symbols_.LogError(file, line, text);
		}

		internal static void LogFatal(string file, int line, string text)
		{
			symbols_.LogFatal(file, line, text);
		}

		internal static void LogInfo(string file, int line, string text)
		{
			symbols_.LogInfo(file, line, text);
		}

		internal static void LogWarning(string file, int line, string text)
		{
			symbols_.LogWarning(file, line, text);
		}

		internal static void MonitorSetName(int i, string name)
		{
			symbols_.MonitorSetName(i, name);
		}

		internal static void MonitorStart(int i)
		{
			symbols_.MonitorStart(i);
		}

		internal static void MonitorStop(int i)
		{
			symbols_.MonitorStop(i);
		}

		internal static WXYZ NavballOrientation(this IntPtr plugin, XYZ sun_world_position, XYZ ship_world_position)
		{
			return symbols_.NavballOrientation(plugin, sun_world_position, ship_world_position);
		}

		internal static IntPtr NewPlugin(string game_epoch, string solar_system_epoch, double planetarium_rotation_in_degrees)
		{
			return symbols_.NewPlugin(game_epoch, solar_system_epoch, planetarium_rotation_in_degrees);
		}

		internal static XYZ PartAngularMomentumFromAngularVelocity(XYZ world_angular_velocity, XYZ moments_of_inertia_in_tonnes, WXYZ principal_axes_rotation, WXYZ part_rotation)
		{
			return symbols_.PartAngularMomentumFromAngularVelocity(world_angular_velocity, moments_of_inertia_in_tonnes, principal_axes_rotation, part_rotation);
		}

		internal static void PartApplyIntrinsicForce(this IntPtr plugin, uint part_id, XYZ force_in_kilonewtons)
		{
			symbols_.PartApplyIntrinsicForce(plugin, part_id, force_in_kilonewtons);
		}

		internal static void PartApplyIntrinsicForceAtPosition(this IntPtr plugin, uint part_id, XYZ force_in_kilonewtons, XYZ lever_arm)
		{
			symbols_.PartApplyIntrinsicForceAtPosition(plugin, part_id, force_in_kilonewtons, lever_arm);
		}

		internal static void PartApplyIntrinsicTorque(this IntPtr plugin, uint part_id, XYZ torque_in_kilonewton_metre)
		{
			symbols_.PartApplyIntrinsicTorque(plugin, part_id, torque_in_kilonewton_metre);
		}

		internal static XYZ PartDragTorqueFromAngularVelocity(double angular_drag_per_second, double delta_t, XYZ world_angular_velocity, XYZ moments_of_inertia_in_tonnes, WXYZ principal_axes_rotation, WXYZ part_rotation)
		{
			return symbols_.PartDragTorqueFromAngularVelocity(angular_drag_per_second, delta_t, world_angular_velocity, moments_of_inertia_in_tonnes, principal_axes_rotation, part_rotation);
		}

		internal static QPRW PartGetActualRigidMotion(this IntPtr plugin, uint part_id, Origin origin)
		{
			return symbols_.PartGetActualRigidMotion(plugin, part_id, origin);
		}

		internal static bool PartIsTruthful(this IntPtr plugin, uint part_id)
		{
			return symbols_.PartIsTruthful(plugin, part_id);
		}

		internal static void PartSetApparentRigidMotion(this IntPtr plugin, uint part_id, QP degrees_of_freedom, WXYZ rotation, XYZ angular_velocity)
		{
			symbols_.PartSetApparentRigidMotion(plugin, part_id, degrees_of_freedom, rotation, angular_velocity);
		}

		internal static DisposablePlanetarium PlanetariumCreate(this IntPtr plugin, XYZ sun_world_position, XYZ xyz_opengl_camera_x_in_world, XYZ xyz_opengl_camera_y_in_world, XYZ xyz_opengl_camera_z_in_world, XYZ xyz_camera_position_in_world, double focal, double field_of_view, double inverse_scale_factor, double angular_resolution, XYZ scaled_space_origin)
		{
			return symbols_.PlanetariumCreate(plugin, sun_world_position, xyz_opengl_camera_x_in_world, xyz_opengl_camera_y_in_world, xyz_opengl_camera_z_in_world, xyz_camera_position_in_world, focal, field_of_view, inverse_scale_factor, angular_resolution, scaled_space_origin);
		}

		internal static void PlanetariumDelete(ref IntPtr planetarium)
		{
			symbols_.PlanetariumDelete(ref planetarium);
		}

		internal static void PlanetariumPlotCelestialFutureTrajectory(this DisposablePlanetarium planetarium, IntPtr plugin, int celestial_index, string vessel_guid, IntPtr vertices, int vertices_size, out double minimal_distance_from_camera, out int vertex_count)
		{
			symbols_.PlanetariumPlotCelestialFutureTrajectory(planetarium, plugin, celestial_index, vessel_guid, vertices, vertices_size, out minimal_distance_from_camera, out vertex_count);
		}

		internal static void PlanetariumPlotCelestialPastTrajectory(this DisposablePlanetarium planetarium, IntPtr plugin, int celestial_index, string vessel_guid, double max_history_length, IntPtr vertices, int vertices_size, out double minimal_distance_from_camera, out int vertex_count)
		{
			symbols_.PlanetariumPlotCelestialPastTrajectory(planetarium, plugin, celestial_index, vessel_guid, max_history_length, vertices, vertices_size, out minimal_distance_from_camera, out vertex_count);
		}

		internal static void PlanetariumPlotEquipotential(this DisposablePlanetarium planetarium, IntPtr plugin, int index, IntPtr vertices, int vertices_size, out int vertex_count)
		{
			symbols_.PlanetariumPlotEquipotential(planetarium, plugin, index, vertices, vertices_size, out vertex_count);
		}

		internal static void PlanetariumPlotFlightPlanSegment(this DisposablePlanetarium planetarium, IntPtr plugin, string vessel_guid, int index, BoxedDouble t_max, IntPtr vertices, int vertices_size, out int vertex_count)
		{
			symbols_.PlanetariumPlotFlightPlanSegment(planetarium, plugin, vessel_guid, index, t_max, vertices, vertices_size, out vertex_count);
		}

		internal static void PlanetariumPlotFlightPlanSegmentDirect(this DisposablePlanetarium planetarium, IntPtr plugin, string vessel_guid, int segment_index, BoxedDouble t_max, IntPtr buffer, int buffer_size, bool is_d3d11_buffer, out int vertex_count)
		{
			symbols_.PlanetariumPlotFlightPlanSegmentDirect(planetarium, plugin, vessel_guid, segment_index, t_max, buffer, buffer_size, is_d3d11_buffer, out vertex_count);
		}

		internal static void PlanetariumPlotPrediction(this DisposablePlanetarium planetarium, IntPtr plugin, string vessel_guid, BoxedDouble t_max, IntPtr vertices, int vertices_size, out int vertex_count)
		{
			symbols_.PlanetariumPlotPrediction(planetarium, plugin, vessel_guid, t_max, vertices, vertices_size, out vertex_count);
		}

		internal static void PlanetariumPlotPredictionDirect(this DisposablePlanetarium planetarium, IntPtr plugin, string vessel_guid, BoxedDouble t_max, IntPtr buffer, int buffer_size, bool is_d3d11_buffer, out int vertex_count)
		{
			symbols_.PlanetariumPlotPredictionDirect(planetarium, plugin, vessel_guid, t_max, buffer, buffer_size, is_d3d11_buffer, out vertex_count);
		}

		internal static void PlanetariumPlotPsychohistory(this DisposablePlanetarium planetarium, IntPtr plugin, string vessel_guid, double max_history_length, BoxedDouble t_max, IntPtr vertices, int vertices_size, out int vertex_count)
		{
			symbols_.PlanetariumPlotPsychohistory(planetarium, plugin, vessel_guid, max_history_length, t_max, vertices, vertices_size, out vertex_count);
		}

		internal static void PlanetariumPlotPsychohistoryDirect(this DisposablePlanetarium planetarium, IntPtr plugin, string vessel_guid, double max_history_length, BoxedDouble t_max, IntPtr buffer, int buffer_size, bool is_d3d11_buffer, out int vertex_count)
		{
			symbols_.PlanetariumPlotPsychohistoryDirect(planetarium, plugin, vessel_guid, max_history_length, t_max, buffer, buffer_size, is_d3d11_buffer, out vertex_count);
		}

		internal static IntPtr PluginReaderAwait(ref IntPtr reader)
		{
			return symbols_.PluginReaderAwait(ref reader);
		}

		internal static IntPtr PluginReaderGet(ref IntPtr reader)
		{
			return symbols_.PluginReaderGet(ref reader);
		}

		internal static string PluginReaderLogs(this IntPtr reader)
		{
			return symbols_.PluginReaderLogs(reader);
		}

		internal static bool PluginReaderWillBeSlow(this IntPtr reader)
		{
			return symbols_.PluginReaderWillBeSlow(reader);
		}

		internal static void PrepareToReportCollisions(this IntPtr plugin)
		{
			symbols_.PrepareToReportCollisions(plugin);
		}

		internal static void RenderedPredictionApsides(this IntPtr plugin, string vessel_guid, BoxedDouble t_max, int celestial_index, XYZ sun_world_position, int max_points, out DisposableIterator apoapsides, out DisposableIterator periapsides)
		{
			symbols_.RenderedPredictionApsides(plugin, vessel_guid, t_max, celestial_index, sun_world_position, max_points, out apoapsides, out periapsides);
		}

		internal static void RenderedPredictionClosestApproaches(this IntPtr plugin, string vessel_guid, XYZ sun_world_position, int max_points, out DisposableIterator closest_approaches)
		{
			symbols_.RenderedPredictionClosestApproaches(plugin, vessel_guid, sun_world_position, max_points, out closest_approaches);
		}

		internal static void RenderedPredictionNodes(this IntPtr plugin, string vessel_guid, BoxedDouble t_max, XYZ sun_world_position, int max_points, out DisposableIterator ascending, out DisposableIterator descending)
		{
			symbols_.RenderedPredictionNodes(plugin, vessel_guid, t_max, sun_world_position, max_points, out ascending, out descending);
		}

		internal static void ReportGroundCollision(this IntPtr plugin, uint part_id)
		{
			symbols_.ReportGroundCollision(plugin, part_id);
		}

		internal static void ReportPartCollision(this IntPtr plugin, uint part1_id, uint part2_id)
		{
			symbols_.ReportPartCollision(plugin, part1_id, part2_id);
		}

		internal static string SayHello()
		{
			return symbols_.SayHello();
		}

		internal static Status SayNotFound()
		{
			return symbols_.SayNotFound();
		}

		internal static string SerializePlugin(this IntPtr plugin, ref IntPtr serializer, string compressor, string encoder)
		{
			return symbols_.SerializePlugin(plugin, ref serializer, compressor, encoder);
		}

		internal static void SetBufferedLogging(int max_severity)
		{
			symbols_.SetBufferedLogging(max_severity);
		}

		internal static void SetFlag(string name, string value)
		{
			symbols_.SetFlag(name, value);
		}

		internal static void SetMainBody(this IntPtr plugin, int index)
		{
			symbols_.SetMainBody(plugin, index);
		}

		internal static void SetPlottingFrame(this IntPtr plugin, PlottingFrameParameters parameters)
		{
			symbols_.SetPlottingFrame(plugin, parameters);
		}

		internal static void SetStderrLogging(int min_severity)
		{
			symbols_.SetStderrLogging(min_severity);
		}

		internal static void SetSuppressedLogging(int min_severity)
		{
			symbols_.SetSuppressedLogging(min_severity);
		}

		internal static void SetTargetVessel(this IntPtr plugin, string vessel_guid, int reference_body_index)
		{
			symbols_.SetTargetVessel(plugin, vessel_guid, reference_body_index);
		}

		internal static void SetVerboseLogging(int level)
		{
			symbols_.SetVerboseLogging(level);
		}

		internal static void SetWorldRotationalReferenceFrame(this IntPtr plugin, int index)
		{
			symbols_.SetWorldRotationalReferenceFrame(plugin, index);
		}

		internal static XYZ UnmanageableVesselVelocity(this IntPtr plugin, QP degrees_of_freedom, int celestial_index)
		{
			return symbols_.UnmanageableVesselVelocity(plugin, degrees_of_freedom, celestial_index);
		}

		internal static void UpdateCelestialHierarchy(this IntPtr plugin, int celestial_index, int parent_index)
		{
			symbols_.UpdateCelestialHierarchy(plugin, celestial_index, parent_index);
		}

		internal static void UpdatePrediction(this IntPtr plugin, string[] vessel_guids)
		{
			symbols_.UpdatePrediction(plugin, vessel_guids);
		}

		internal static XYZ VesselBinormal(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.VesselBinormal(plugin, vessel_guid);
		}

		internal static void VesselClearPlottingFramePayload(this IntPtr plugin, string vessel_guid, PlottingFrameParameters parameters)
		{
			symbols_.VesselClearPlottingFramePayload(plugin, vessel_guid, parameters);
		}

		internal static QP VesselFromParent(this IntPtr plugin, int parent_index, string vessel_guid)
		{
			return symbols_.VesselFromParent(plugin, parent_index, vessel_guid);
		}

		internal static OrbitAnalysis VesselGetAnalysis(this IntPtr plugin, string vessel_guid, BoxedInt32 revolutions_per_cycle, BoxedInt32 days_per_cycle, int ground_track_revolution)
		{
			return symbols_.VesselGetAnalysis(plugin, vessel_guid, revolutions_per_cycle, days_per_cycle, ground_track_revolution);
		}

		internal static PlottingFramePayload VesselGetPlottingFramePayload(this IntPtr plugin, string vessel_guid, PlottingFrameParameters parameters)
		{
			return symbols_.VesselGetPlottingFramePayload(plugin, vessel_guid, parameters);
		}

		internal static AdaptiveStepParameters VesselGetPredictionAdaptiveStepParameters(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.VesselGetPredictionAdaptiveStepParameters(plugin, vessel_guid);
		}

		internal static XYZ VesselNormal(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.VesselNormal(plugin, vessel_guid);
		}

		internal static void VesselRequestAnalysis(this IntPtr plugin, string vessel_guid, double mission_duration)
		{
			symbols_.VesselRequestAnalysis(plugin, vessel_guid, mission_duration);
		}

		internal static void VesselSetPlottingFramePayload(this IntPtr plugin, string vessel_guid, PlottingFrameParameters parameters, PlottingFramePayload payload)
		{
			symbols_.VesselSetPlottingFramePayload(plugin, vessel_guid, parameters, payload);
		}

		internal static void VesselSetPredictionAdaptiveStepParameters(this IntPtr plugin, string vessel_guid, AdaptiveStepParameters adaptive_step_parameters)
		{
			symbols_.VesselSetPredictionAdaptiveStepParameters(plugin, vessel_guid, adaptive_step_parameters);
		}

		internal static XYZ VesselTangent(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.VesselTangent(plugin, vessel_guid);
		}

		internal static XYZ VesselVelocity(this IntPtr plugin, string vessel_guid)
		{
			return symbols_.VesselVelocity(plugin, vessel_guid);
		}

		internal static IntPtr StateBusCreateChannel()
		{
			return symbols_?.StateBusCreateChannel != null ? symbols_.StateBusCreateChannel() : IntPtr.Zero;
		}

		internal static void StateBusDestroyChannel(ref IntPtr channel)
		{
			symbols_?.StateBusDestroyChannel?.Invoke(ref channel);
		}

		internal static void StateBusCommitWrite(IntPtr channel, ref DensePolynomialSlice slice)
		{
			symbols_?.StateBusCommitWrite?.Invoke(channel, ref slice);
		}

		internal static bool StateBusAcquireRead(IntPtr channel, out DensePolynomialSlice out_slice)
		{
			if (symbols_?.StateBusAcquireRead != null) {
				return symbols_.StateBusAcquireRead(channel, out out_slice);
			}
			out_slice = default(DensePolynomialSlice);
			return false;
		}

		internal static IntPtr CommandBusCreate()
		{
			return symbols_?.CommandBusCreate != null ? symbols_.CommandBusCreate() : IntPtr.Zero;
		}

		internal static void CommandBusDestroy(ref IntPtr queue)
		{
			symbols_?.CommandBusDestroy?.Invoke(ref queue);
		}

		internal static bool CommandBusTryPush(IntPtr queue, ref CommandPacket packet)
		{
			return symbols_?.CommandBusTryPush != null && symbols_.CommandBusTryPush(queue, ref packet);
		}

		internal static bool CommandBusTryPop(IntPtr queue, out CommandPacket out_packet)
		{
			if (symbols_?.CommandBusTryPop != null) {
				return symbols_.CommandBusTryPop(queue, out out_packet);
			}
			out_packet = default(CommandPacket);
			return false;
		}

		internal static void BatchInsertOrKeepLoadedParts(this IntPtr plugin, PartBatchInsertState[] states, int count)
		{
			symbols_?.BatchInsertOrKeepLoadedParts?.Invoke(plugin, states, count);
		}

		internal static void BatchPartSetApparentRigidMotion(this IntPtr plugin, PartBatchApparentMotion[] states, int count)
		{
			symbols_?.BatchPartSetApparentRigidMotion?.Invoke(plugin, states, count);
		}

		internal static void BatchPartGetActualRigidMotion(this IntPtr plugin, ref Origin origin, uint[] part_ids, QPRW[] motions_out, int count)
		{
			symbols_?.BatchPartGetActualRigidMotion?.Invoke(plugin, ref origin, part_ids, motions_out, count);
		}


	}
}
}
