using System;

namespace Alethic.EcmaScript.Hosting;

/// <summary>
/// Kind of a <see cref="JavaScriptValue"/>.
/// </summary>
public enum JavaScriptValueKind
{

	/// <summary>
	/// The engine's <c>undefined</c>.
	/// </summary>
	Undefined,

	/// <summary>
	/// The engine's <c>null</c>.
	/// </summary>
	Null,

	/// <summary>
	/// A boolean.
	/// </summary>
	Boolean,

	/// <summary>
	/// A number.
	/// </summary>
	Number,

	/// <summary>
	/// A string.
	/// </summary>
	String,

	/// <summary>
	/// Anything structured — objects, arrays, functions, promises, streams — held by reference as an
	/// <see cref="IJavaScriptObject"/> rather than copied.
	/// </summary>
	Object,

}

/// <summary>
/// A JavaScript value as it crosses to .NET: primitives by value, everything else by reference.
/// </summary>
/// <remarks>
/// This is the whole marshalling story, deliberately. Primitives convert because both sides agree
/// what they are; structured values stay in the engine and are operated on through their handle.
/// Nothing is serialized on the way in or out — a consumer that wants JSON asks the engine's own
/// <c>JSON</c> for it, like any other call.
/// </remarks>
public readonly struct JavaScriptValue
{

	/// <summary>
	/// The engine's <c>undefined</c>.
	/// </summary>
	public static readonly JavaScriptValue Undefined = new(JavaScriptValueKind.Undefined, default, default, null, null);

	/// <summary>
	/// The engine's <c>null</c>.
	/// </summary>
	public static readonly JavaScriptValue Null = new(JavaScriptValueKind.Null, default, default, null, null);

	readonly JavaScriptValueKind kind;
	readonly bool boolean;
	readonly double number;
	readonly string? text;
	readonly IJavaScriptObject? instance;

	JavaScriptValue(JavaScriptValueKind kind, bool boolean, double number, string? text, IJavaScriptObject? instance)
	{
		this.kind = kind;
		this.boolean = boolean;
		this.number = number;
		this.text = text;
		this.instance = instance;
	}

	/// <summary>
	/// Wraps a boolean.
	/// </summary>
	/// <param name="value"></param>
	public static implicit operator JavaScriptValue(bool value) => new(JavaScriptValueKind.Boolean, value, default, null, null);

	/// <summary>
	/// Wraps a number.
	/// </summary>
	/// <param name="value"></param>
	public static implicit operator JavaScriptValue(double value) => new(JavaScriptValueKind.Number, default, value, null, null);

	/// <summary>
	/// Wraps a number.
	/// </summary>
	/// <param name="value"></param>
	public static implicit operator JavaScriptValue(int value) => new(JavaScriptValueKind.Number, default, value, null, null);

	/// <summary>
	/// Wraps a string; null becomes <see cref="Null"/>.
	/// </summary>
	/// <param name="value"></param>
	public static implicit operator JavaScriptValue(string? value) => value is null ? Null : new(JavaScriptValueKind.String, default, default, value, null);

	/// <summary>
	/// Wraps an object handle; null becomes <see cref="Null"/>.
	/// </summary>
	/// <param name="value"></param>
	public static JavaScriptValue From(IJavaScriptObject? value) => value is null ? Null : new(JavaScriptValueKind.Object, default, default, null, value);

	/// <summary>
	/// Kind of this value.
	/// </summary>
	public JavaScriptValueKind Kind => kind;

	/// <summary>
	/// True for <see cref="JavaScriptValueKind.Undefined"/> and <see cref="JavaScriptValueKind.Null"/>.
	/// </summary>
	public bool IsNullish => kind is JavaScriptValueKind.Undefined or JavaScriptValueKind.Null;

	/// <summary>
	/// The value as a boolean.
	/// </summary>
	/// <exception cref="InvalidOperationException"></exception>
	public bool AsBoolean() => kind == JavaScriptValueKind.Boolean ? boolean : throw Wrong(JavaScriptValueKind.Boolean);

	/// <summary>
	/// The value as a number.
	/// </summary>
	/// <exception cref="InvalidOperationException"></exception>
	public double AsNumber() => kind == JavaScriptValueKind.Number ? number : throw Wrong(JavaScriptValueKind.Number);

	/// <summary>
	/// The value as a string.
	/// </summary>
	/// <exception cref="InvalidOperationException"></exception>
	public string AsString() => kind == JavaScriptValueKind.String ? text! : throw Wrong(JavaScriptValueKind.String);

	/// <summary>
	/// The value as an object handle.
	/// </summary>
	/// <exception cref="InvalidOperationException"></exception>
	public IJavaScriptObject AsObject() => kind == JavaScriptValueKind.Object ? instance! : throw Wrong(JavaScriptValueKind.Object);

	/// <summary>
	/// Builds the mismatch error.
	/// </summary>
	/// <param name="expected"></param>
	InvalidOperationException Wrong(JavaScriptValueKind expected) => new($"The value is {kind}, not {expected}.");

	/// <inheritdoc />
	public override string ToString() => kind switch
	{
		JavaScriptValueKind.Undefined => "undefined",
		JavaScriptValueKind.Null => "null",
		JavaScriptValueKind.Boolean => boolean ? "true" : "false",
		JavaScriptValueKind.Number => number.ToString(),
		JavaScriptValueKind.String => text!,
		_ => instance!.ToString() ?? "[object]",
	};

}
