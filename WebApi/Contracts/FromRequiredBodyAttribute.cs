using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace WebApi.Contracts;

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromRequiredBodyAttribute : FromBodyAttribute
{
    public FromRequiredBodyAttribute()
    {
        // решение: пустое тело передаем в валидацию обязательного non-nullable параметра,
        // чтобы запрос без тела и Content-Type получил 400 вместо 415.
        EmptyBodyBehavior = EmptyBodyBehavior.Allow;
    }
}
