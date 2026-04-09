using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Adds a specific collider component to a GameObject.
/// Supports Box, Sphere, Capsule, Mesh, and Hull collider types.
/// Optionally configures as trigger and sets dimensions.
/// </summary>
public class AddColliderHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		var id = parameters.GetProperty( "id" ).GetString()
			?? throw new Exception( "Missing required parameter: id" );
		var type = parameters.GetProperty( "type" ).GetString()
			?? throw new Exception( "Missing required parameter: type" );

		if ( !Guid.TryParse( id, out var guid ) )
			throw new Exception( $"Invalid GUID: {id}" );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			throw new Exception( $"GameObject not found: {id}" );

		var isTrigger = parameters.TryGetProperty( "isTrigger", out var trigProp )
			&& trigProp.GetBoolean();

		Collider collider;
		switch ( type.ToLowerInvariant() )
		{
			case "box":
				var box = go.Components.Create<BoxCollider>();
				if ( parameters.TryGetProperty( "size", out var sizeProp ) )
					box.Scale = CreateGameObjectHandler.ParseVector3( sizeProp );
				collider = box;
				break;

			case "sphere":
				var sphere = go.Components.Create<SphereCollider>();
				if ( parameters.TryGetProperty( "radius", out var radiusProp ) )
					sphere.Radius = radiusProp.GetSingle();
				collider = sphere;
				break;

			case "capsule":
				var capsule = go.Components.Create<CapsuleCollider>();
				if ( parameters.TryGetProperty( "radius", out var capRadProp ) )
					capsule.Radius = capRadProp.GetSingle();
				if ( parameters.TryGetProperty( "height", out var heightProp ) )
					capsule.Length = heightProp.GetSingle();
				collider = capsule;
				break;

			case "mesh":
				collider = go.Components.Create<MeshCollider>();
				break;

			case "hull":
				collider = go.Components.Create<HullCollider>();
				break;

			default:
				throw new Exception( $"Unknown collider type: {type}. Use: box, sphere, capsule, mesh, hull" );
		}

		collider.IsTrigger = isTrigger;

		return Task.FromResult<object>( new
		{
			id,
			gameObject = go.Name,
			colliderType = collider.GetType().Name,
			isTrigger,
			added = true,
		} );
	}
}
