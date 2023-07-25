# HotChocolate Middleware Parser
A C# parser to manually handle HotChocolate's middleware when your data source is not the same type as HotChocolate is expecting or for whatever reason needs to be done before the end of your graphql endpoint.

# Use
You will need to add `IFilterContext`, `ISortingContext`, and `IResolverContext` to the parameters of your graphql endpoint like so
```csharp
[UsePaging]
[UseProjection]
[UseFiltering]
[UseSorting]
public IQueryable<Item> Items(DataSource ItemsData,
                              IFilterContext filterContext,
                              ISortingContext sortingContext,
                              IResolverContext resolver)
{
    // ...
}
```
Then just pass them into the constructor for the `HotChocolateMiddlewareParser`
```csharp
var parser = new HotChocolateMiddlewareParser<Item>(ItemsData.Items, resolver, filterContext, sortingContext);
```
You can also provide a default value for the `first` property of paging (this is optional, the default is 10) which will be used in the Linq `Take()` function if Hotchocolate does not provide one
```csharp
var parser = new HotChocolateMiddlewareParser<Item>(ItemsData.Items, resolver, filterContext, sortingContext, 25);
```
If the type of your data source is not the same as the type HotChocolate is expecting and the two types do not have the same property names, you can provide a `Dictionary<string, string>` to map the property names from one type to the other. The keys of the dictionary should be the names of the property on the type HotChocolate is expecting and the values should be the names of the property on the generic type for the parser.
```csharp
var propertyMapper = new Dictionary<string, string>()
{
    { "Property1", "AltProperty1" },
    { "Property2", "AltProperty2" },
    { "Property3", "AltProperty3" },
    { "Property4", "AltProperty4" }
};
var parser = new HotChocolateMiddlewareParser<Item>(ItemsData.Items, resolver, filterContext, sortingContext, 25, propertyMapper);
```
Finally you can call `Parse` on the parser to get the handled `IQueryable<Item>`
```csharp
IQueryable<Item> result = parser.Parse()
```
or put it all together
```csharp
IQueryable<Item> result = new HotChocolateMiddlewareParser<Item>(ItemsData.Items, resolver, filterContext, sortingContext, 25, propertyMapper).Parse();
```
